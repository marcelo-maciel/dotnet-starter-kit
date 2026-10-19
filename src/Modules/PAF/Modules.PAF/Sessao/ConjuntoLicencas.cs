using System.Globalization;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace FSH.Modules.PAF.Sessao;

/// <summary>Unidade candidata à ocupação, na ordem da cascata; <c>Limite</c> e política vêm do contrato vigente.</summary>
public sealed record CandidataLicenca(Guid UnidadeId, int Limite, bool UltrapassaComAviso);

public enum DesfechoOcupacao
{
    Ocupada,
    JaOcupava,
    SemSaldo,

    /// <summary>Valkey alcançável, mas os conjuntos ainda não foram reconstruídos depois de perder os dados.</summary>
    NaoReconstruido,

    /// <summary>Valkey ausente, desconectado ou acima do prazo: a decisão vai para o modo degradado.</summary>
    Indisponivel,
}

/// <summary><c>Ocupadas</c> conta a vaga recém-ocupada; acima do limite só com ultrapassagem contratada.</summary>
public readonly record struct Ocupacao(DesfechoOcupacao Desfecho, Guid? UnidadeId, int Ocupadas);

/// <summary>Sessão que a reconstrução devolve ao conjunto, com o prazo que resta à marca no PostgreSQL.</summary>
public readonly record struct MembroVivo(Guid SessaoId, Guid UnidadeId, TimeSpan Restante);

/// <summary>
/// Conjunto de licenças no Valkey (seção 9, RF22): um ZSET por tenant e unidade cujo score é o vencimento do
/// membro, no relógio do Valkey, para todas as instâncias contarem pelo mesmo relógio. Cada operação é um script
/// só, porque ocupar e soltar em chamadas separadas cobraria duas licenças de uma pessoa ou entregaria uma de graça.
/// O índice <c>paf:{t}:sessao:{s}:licenca</c> diz em que unidade a sessão está, para a ocupação ser idempotente.
/// </summary>
public sealed class ConjuntoLicencas(
    IEnumerable<IConnectionMultiplexer> multiplexers,
    TimeSpan prazoMembro,
    ILogger<ConjuntoLicencas> logger)
{
    public const string PrefixoReserva = "reserva:";

    // ponytail: fixed per-call ceiling, same as EstadoSessao; tune once production Valkey latency is measured.
    private static readonly TimeSpan PrazoChamada = TimeSpan.FromMilliseconds(500);

    // Global, not per tenant: losing the data loses every tenant at once, and the rebuild covers them all.
    private static readonly RedisKey ChaveReconstruido = "paf:licencas:reconstruido";

    private readonly IConnectionMultiplexer? _valkey = multiplexers.FirstOrDefault();

    public TimeSpan PrazoMembro => prazoMembro;

    public static string Membro(Guid sessaoId) => sessaoId.ToString();

    public static string NovaReserva() => PrefixoReserva + Guid.NewGuid().ToString("N");

    // Keys of the members to release and of the candidate sets are built inside the scripts from this prefix.
    // ponytail: valid on the standalone Valkey of every environment; a cluster needs all keys declared or one
    // hash slot per tenant.
    private static string Prefixo(string tenantId) => $"paf:{tenantId}:";

    private static RedisKey ChaveIndice(string tenantId, string membro) => $"paf:{tenantId}:sessao:{membro}:licenca";

    private static RedisKey ChaveRegistroUnidades(string tenantId) => $"paf:{tenantId}:unidades-com-licenca";

    public static RedisKey ChaveConjunto(string tenantId, Guid unidadeId) => $"paf:{tenantId}:licencas:{unidadeId}";

    private const string Relogio = """
        local t = redis.call('TIME')
        local agora = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local function prolongar(conj, validade)
          if redis.call('PTTL', conj) < validade then redis.call('PEXPIRE', conj, validade) end
        end
        """;

    // A raw literal drops its last line break; without it the next script's first line would glue to "end".
    private const string Quebra = "\n";

    // KEYS: reconstruido, indice do membro, registro de unidades do tenant.
    // ARGV: prefixo, membro, validade ms, n ignorar, ignorar..., n soltar, soltar..., n candidatas, (unidade, limite, ultrapassa)...
    private const string ScriptOcupar = Relogio + Quebra + """
        if redis.call('EXISTS', KEYS[1]) == 0 then return {'naoreconstruido'} end
        local p = ARGV[1]
        local membro = ARGV[2]
        local validade = tonumber(ARGV[3])
        local i = 4
        local ignorar = {}
        local n = tonumber(ARGV[i])
        for k = 1, n do ignorar[#ignorar + 1] = ARGV[i + k] end
        i = i + n + 1
        n = tonumber(ARGV[i])
        for k = 1, n do
          local idx = p .. 'sessao:' .. ARGV[i + k] .. ':licenca'
          local u = redis.call('GET', idx)
          if u then
            redis.call('ZREM', p .. 'licencas:' .. u, ARGV[i + k])
            redis.call('DEL', idx)
          end
        end
        i = i + n + 1
        local atual = redis.call('GET', KEYS[2])
        if atual then
          local conj = p .. 'licencas:' .. atual
          local score = redis.call('ZSCORE', conj, membro)
          if score and tonumber(score) > agora then
            redis.call('ZADD', conj, agora + validade, membro)
            redis.call('PEXPIRE', KEYS[2], validade)
            prolongar(conj, validade)
            return {'ja', atual, tostring(redis.call('ZCARD', conj))}
          end
        end
        n = tonumber(ARGV[i])
        for k = 0, n - 1 do
          local u = ARGV[i + 1 + 3 * k]
          local limite = tonumber(ARGV[i + 2 + 3 * k])
          local conj = p .. 'licencas:' .. u
          redis.call('ZREMRANGEBYSCORE', conj, '-inf', agora)
          local vivos = redis.call('ZCARD', conj)
          for _, s in ipairs(ignorar) do
            if redis.call('ZSCORE', conj, s) then vivos = vivos - 1 end
          end
          if vivos < limite or ARGV[i + 3 + 3 * k] == '1' then
            redis.call('ZADD', conj, agora + validade, membro)
            redis.call('SET', KEYS[2], u, 'PX', validade)
            redis.call('SADD', KEYS[3], u)
            prolongar(conj, validade)
            return {'ocupada', u, tostring(vivos + 1)}
          end
        end
        return {'semsaldo'}
        """;

    // Puts the member in the given unit whatever the balance: the PostgreSQL row says the session holds it there.
    // KEYS: indice, registro de unidades. ARGV: prefixo, membro, unidade, validade ms.
    private const string ScriptRestaurar = Relogio + Quebra + """
        local atual = redis.call('GET', KEYS[1])
        if atual and atual ~= ARGV[3] then redis.call('ZREM', ARGV[1] .. 'licencas:' .. atual, ARGV[2]) end
        local conj = ARGV[1] .. 'licencas:' .. ARGV[3]
        local validade = tonumber(ARGV[4])
        redis.call('ZADD', conj, agora + validade, ARGV[2])
        redis.call('SET', KEYS[1], ARGV[3], 'PX', validade)
        redis.call('SADD', KEYS[2], ARGV[3])
        prolongar(conj, validade)
        return 1
        """;

    // KEYS: indice. ARGV: prefixo, membro.
    private const string ScriptLiberar = """
        local u = redis.call('GET', KEYS[1])
        if not u then return 0 end
        redis.call('ZREM', ARGV[1] .. 'licencas:' .. u, ARGV[2])
        redis.call('DEL', KEYS[1])
        return 1
        """;

    // One unit set from the PostgreSQL truth. Members untouched since the snapshot (score below marco) and absent
    // from the list leave; members occupied or renewed after the snapshot, and reservations, stay.
    // KEYS: conjunto, registro de unidades. ARGV: prefixo, unidade, marco ms, (sessao, restante ms)...
    private const string ScriptReconstruir = Relogio + Quebra + """
        local p = ARGV[1]
        local manter = {}
        for k = 4, #ARGV, 2 do
          local s = ARGV[k]
          local restante = tonumber(ARGV[k + 1])
          manter[s] = true
          redis.call('ZADD', KEYS[1], 'GT', agora + restante, s)
          redis.call('SET', p .. 'sessao:' .. s .. ':licenca', ARGV[2], 'PX', restante)
          prolongar(KEYS[1], restante)
        end
        if #ARGV >= 4 then redis.call('SADD', KEYS[2], ARGV[2]) end
        for _, s in ipairs(redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', '(' .. ARGV[3])) do
          if not manter[s] and string.sub(s, 1, 8) ~= 'reserva:' then
            redis.call('ZREM', KEYS[1], s)
            local idx = p .. 'sessao:' .. s .. ':licenca'
            if redis.call('GET', idx) == ARGV[2] then redis.call('DEL', idx) end
          end
        end
        return 1
        """;

    // Live session members per unit, reservations left out: the sample counts sessions, and a reservation becomes one
    // or leaves within PrazoReserva. Reads only. KEYS: reconstruido. ARGV: prefixo, unidades...
    private const string ScriptTamanhos = Relogio + Quebra + """
        if redis.call('EXISTS', KEYS[1]) == 0 then return {'naoreconstruido'} end
        local r = {'ok'}
        for k = 2, #ARGV do
          local n = 0
          for _, s in ipairs(redis.call('ZRANGEBYSCORE', ARGV[1] .. 'licencas:' .. ARGV[k], '(' .. agora, '+inf')) do
            if string.sub(s, 1, 8) ~= 'reserva:' then n = n + 1 end
          end
          r[#r + 1] = tostring(n)
        end
        return r
        """;

    private const string ScriptAgora = "local t = redis.call('TIME') return tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)";

    /// <summary>
    /// Solta <paramref name="soltar"/>, devolve a unidade em que <paramref name="membro"/> já está (idempotente) ou
    /// ocupa a primeira candidata com saldo, contando os membros de <paramref name="ignorar"/> como livres.
    /// </summary>
    public async Task<Ocupacao> OcuparAsync(
        string tenantId,
        string membro,
        IReadOnlyList<CandidataLicenca> candidatas,
        IReadOnlyCollection<string> ignorar,
        IReadOnlyCollection<string> soltar,
        TimeSpan validade,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidatas);
        ArgumentNullException.ThrowIfNull(ignorar);
        ArgumentNullException.ThrowIfNull(soltar);
        var argumentos = new List<RedisValue> { Prefixo(tenantId), membro, Milissegundos(validade), ignorar.Count };
        argumentos.AddRange(ignorar.Select(s => (RedisValue)s));
        argumentos.Add(soltar.Count);
        argumentos.AddRange(soltar.Select(s => (RedisValue)s));
        argumentos.Add(candidatas.Count);
        foreach (var candidata in candidatas)
        {
            argumentos.Add(candidata.UnidadeId.ToString());
            argumentos.Add(candidata.Limite);
            argumentos.Add(candidata.UltrapassaComAviso ? "1" : "0");
        }

        var chaves = new[] { ChaveReconstruido, ChaveIndice(tenantId, membro), ChaveRegistroUnidades(tenantId) };
        var resultado = await TentarAsync(db => db.ScriptEvaluateAsync(ScriptOcupar, chaves, [.. argumentos]), ct);
        if (resultado is null)
        {
            return new Ocupacao(DesfechoOcupacao.Indisponivel, null, 0);
        }

        var partes = ((RedisResult[]?)resultado ?? []).Select(p => (string?)p ?? string.Empty).ToArray();
        return partes.FirstOrDefault() switch
        {
            "ocupada" => new Ocupacao(DesfechoOcupacao.Ocupada, Guid.Parse(partes[1]), int.Parse(partes[2], CultureInfo.InvariantCulture)),
            "ja" => new Ocupacao(DesfechoOcupacao.JaOcupava, Guid.Parse(partes[1]), int.Parse(partes[2], CultureInfo.InvariantCulture)),
            "semsaldo" => new Ocupacao(DesfechoOcupacao.SemSaldo, null, 0),
            "naoreconstruido" => new Ocupacao(DesfechoOcupacao.NaoReconstruido, null, 0),
            var outro => throw new InvalidOperationException($"Licence set script returned '{outro}'."),
        };
    }

    /// <summary>Renova o membro na unidade da linha do PostgreSQL, devolvendo-o a ela se tinha sumido. Falso sem Valkey.</summary>
    public async Task<bool> RestaurarAsync(string tenantId, string membro, Guid unidadeId, TimeSpan validade, CancellationToken ct) =>
        await TentarAsync(
            db => db.ScriptEvaluateAsync(
                ScriptRestaurar,
                [ChaveIndice(tenantId, membro), ChaveRegistroUnidades(tenantId)],
                [Prefixo(tenantId), membro, unidadeId.ToString(), Milissegundos(validade)]),
            ct) is not null;

    /// <summary>Falso sem Valkey: o membro some sozinho no vencimento.</summary>
    public async Task<bool> LiberarAsync(string tenantId, string membro, CancellationToken ct) =>
        await TentarAsync(
            db => db.ScriptEvaluateAsync(ScriptLiberar, [ChaveIndice(tenantId, membro)], [Prefixo(tenantId), membro]),
            ct) is not null;

    /// <summary>
    /// Solta, sem esperar, o membro de uma ocupação que passou do prazo. Um Valkey travado (pausado, rede presa)
    /// executa o script pendente quando volta; esta liberação vai na mesma conexão, atrás dele, e o desfaz.
    /// </summary>
    public void SoltarOcupacaoSemResposta(string tenantId, string membro)
    {
        if (_valkey is not { IsConnected: true } valkey)
        {
            return;
        }

        // FireAndForget: queued in order on the same connection, no wait, no reply to observe.
        _ = valkey.GetDatabase().ScriptEvaluateAsync(
            ScriptLiberar, [ChaveIndice(tenantId, membro)], [Prefixo(tenantId), membro], CommandFlags.FireAndForget);
    }

    /// <summary>
    /// Sessões vivas no conjunto de cada unidade, na ordem dada; nulo sem Valkey ou antes da reconstrução, quando o
    /// conjunto não é a contagem que vale (modo degradado).
    /// </summary>
    public async Task<IReadOnlyList<int>?> TamanhosAsync(string tenantId, IReadOnlyList<Guid> unidades, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unidades);
        var argumentos = new List<RedisValue> { Prefixo(tenantId) };
        argumentos.AddRange(unidades.Select(u => (RedisValue)u.ToString()));
        var resultado = await TentarAsync(db => db.ScriptEvaluateAsync(ScriptTamanhos, [ChaveReconstruido], [.. argumentos]), ct);
        if (resultado is null)
        {
            return null;
        }

        var partes = ((RedisResult[]?)resultado ?? []).Select(p => (string?)p ?? string.Empty).ToArray();
        if (partes.FirstOrDefault() != "ok")
        {
            return null;
        }

        return partes.Skip(1).Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>Nulo sem Valkey.</summary>
    public async Task<bool?> ReconstruidoAsync(CancellationToken ct)
    {
        var resultado = await TentarAsync(async db => RedisResult.Create(await db.KeyExistsAsync(ChaveReconstruido)), ct);
        return resultado is null ? null : (bool)resultado;
    }

    /// <summary>Instante do relógio do Valkey em milissegundos, tirado antes de ler o PostgreSQL; nulo sem Valkey.</summary>
    public async Task<long?> MarcoAsync(CancellationToken ct)
    {
        var resultado = await TentarAsync(db => db.ScriptEvaluateAsync(ScriptAgora), ct);
        return resultado is null ? null : (long)resultado;
    }

    /// <summary>
    /// Reconstrói os conjuntos de um tenant a partir dos membros vivos no PostgreSQL. As unidades sem membro vivo
    /// também passam, para perder os membros de sessões que acabaram durante a queda. Falso sem Valkey.
    /// </summary>
    public async Task<bool> ReconstruirTenantAsync(string tenantId, IReadOnlyCollection<MembroVivo> vivos, long marco, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vivos);
        var registradas = await TentarAsync(
            async db => RedisResult.Create(await db.SetMembersAsync(ChaveRegistroUnidades(tenantId))),
            ct);
        if (registradas is null)
        {
            return false;
        }

        var unidades = ((RedisValue[]?)registradas ?? [])
            .Select(v => Guid.Parse(v.ToString()))
            .Concat(vivos.Select(v => v.UnidadeId))
            .Distinct();
        // The cutoff is the score of a member touched exactly at the snapshot instant.
        var limiteToque = marco + Milissegundos(prazoMembro);
        foreach (var unidadeId in unidades)
        {
            var argumentos = new List<RedisValue> { Prefixo(tenantId), unidadeId.ToString(), limiteToque };
            foreach (var vivo in vivos.Where(v => v.UnidadeId == unidadeId))
            {
                argumentos.Add(Membro(vivo.SessaoId));
                argumentos.Add(Milissegundos(vivo.Restante));
            }

            var ok = await TentarAsync(
                db => db.ScriptEvaluateAsync(
                    ScriptReconstruir,
                    [ChaveConjunto(tenantId, unidadeId), ChaveRegistroUnidades(tenantId)],
                    [.. argumentos]),
                ct);
            if (ok is null)
            {
                return false;
            }
        }

        return true;
    }

    public async Task<bool> MarcarReconstruidoAsync(CancellationToken ct) =>
        await TentarAsync(async db => RedisResult.Create(await db.StringSetAsync(ChaveReconstruido, "1")), ct) is not null;

    /// <summary>Disparado quando a conexão ao Valkey volta; nada acontece sem Valkey registrado.</summary>
    public void AoReconectar(Action acao)
    {
        if (_valkey is not null)
        {
            _valkey.ConnectionRestored += (_, _) => acao();
        }
    }

    // Whole milliseconds, at least one: PX 0 is an error and would abort the script.
    private static long Milissegundos(TimeSpan prazo) => Math.Max(1, (long)prazo.TotalMilliseconds);

    // Null means unavailable. Only connection and timeout failures degrade; a server error (a broken script)
    // propagates, because turning it into the degraded mode would hide it.
    private async Task<RedisResult?> TentarAsync(Func<IDatabase, Task<RedisResult>> operacao, CancellationToken ct)
    {
        if (_valkey is not { IsConnected: true } valkey)
        {
            return null;
        }

        try
        {
            return await operacao(valkey.GetDatabase()).WaitAsync(PrazoChamada, ct);
        }
        catch (Exception ex) when (ex is RedisConnectionException or TimeoutException)
        {
            logger.LogWarning(ex, "Valkey indisponível para o conjunto de licenças; modo degradado");
            return null;
        }
    }
}
