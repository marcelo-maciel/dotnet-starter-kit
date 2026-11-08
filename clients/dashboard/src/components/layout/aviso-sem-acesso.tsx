// Notice shown in place of a screen the signed-in user lacks the permission for. The route renders it instead of
// mounting the page, so the page never calls an API that would answer 403.
export function AvisoSemAcesso({ texto }: { texto: string }) {
  return (
    <p role="alert" data-sem-acesso className="rounded-md border border-[var(--color-border)] px-4 py-3">
      {texto}
    </p>
  );
}
