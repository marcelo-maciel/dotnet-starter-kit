# Localization (i18n)

`src/BuildingBlocks/Core/Localization/` + per-module `Localization/` folders. Read before adding any user-facing message (exception, validation, API error). **The client's culture is always respected, never the server's.**

## Culture negotiation (already wired — don't re-add)

`AddHeroLocalization()` / `UseHeroLocalization()` (`BuildingBlocks/Web/Localization/`) negotiate the request culture in this order: `?culture=` query → `locale` JWT claim (`UserLocaleRequestCultureProvider`) → `Accept-Language` → configured default → `en-US`. Supported tags live in `SupportedCultures`. The culture is set before endpoints and the exception handler run, so any `IStringLocalizer` resolved downstream picks up the request culture automatically.

## Catalogs — hybrid, one marker per catalog

- **Core (`SharedResources`)** — generic / cross-cutting messages: ProblemDetails titles (`Error.*`), cross-module errors (`Error.TenantContextRequired`, `Error.NoCurrentUser`, …), and shared validation (`Validation.*`).
- **Per module (`<Module>Resources`)** — domain-specific messages owned by the module: `src/Modules/<Module>/Modules.<Module>/Localization/<Module>Resources.cs` (marker `public sealed class <Module>Resources;`) + co-located `<Module>Resources.resx` (en) + `<Module>Resources.pt.resx` (pt). `ResourcesPath = ""` (co-located), so the resx manifest name must equal the marker's full type name.

Key naming: `Error.<Module>.<Case>` for domain messages (`Catalog.ProductNotFound`), `Error.<CrossCutting>` / `Validation.<Case>` for Core. PascalCase. Placeholders are `{0}`, `{1}` (`string.Format` via the localizer) — **not** the frontend's `{{name}}`.

## Exceptions — localize at the boundary, log stays English

Throw with the **English message** as `Exception.Message` (used for logs and fallback) plus the resource key metadata. **Never** pre-localize the message at the throw site.

```csharp
// domain message -> module catalog
throw new NotFoundException($"Product {id} not found.")
{
    MessageKey = "Catalog.ProductNotFound",
    MessageArgs = [id],
    ResourceSource = typeof(CatalogResources),
};

// cross-cutting message -> Core catalog (ResourceSource omitted = SharedResources)
throw new UnauthorizedException("Tenant context is required.")
{
    MessageKey = "Error.TenantContextRequired",
};
```

`GlobalExceptionHandler` resolves `Title` (by status) and `Detail` (via `MessageKey` + `ResourceSource`) under the request culture, and falls back to `Exception.Message` when the key is missing (`ResourceNotFound`) or malformed (`FormatException`). Migration is therefore incremental: an un-migrated `throw new NotFoundException("...")` still renders its English literal.

**Do NOT** set `ProblemDetails` from a localized string in logs — the handler logs `Exception.Message` (English) and the type name, never the translated body.

## Validators — inject the localizer, defer resolution

```csharp
public sealed class XCommandValidator : AbstractValidator<XCommand>
{
    public XCommandValidator(IStringLocalizer<SharedResources> localizer)
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage(_ => localizer["Validation.NameRequired"]);
    }
}
```

Always the `.WithMessage(_ => localizer["Key"])` lambda (resolution is deferred to `Validate()`, under the request culture) — never `.WithMessage(localizer["Key"])`. **Catalog choice:** inject `IStringLocalizer<SharedResources>` for genuinely shared/generic validation (`Validation.*` already in Core, reuse them), or `IStringLocalizer<<Module>Resources>` for module-specific validation messages kept in the module's own catalog. DI provides the localizer automatically (`AddValidatorsFromAssembly` + `AddHeroLocalization` + the module's own `AddLocalization`); nested validators (`Include(new PagedQueryValidator<T>(localizer))`) receive it from the parent.

## Tests (required with every catalog change)

- **Parity** — every key present in both `en` and `pt` for each catalog (Core + every `<Module>Resources`). Extend the parity test when adding a module catalog.
- **Code → resx guard** — every referenced key (`MessageKey`, `localizer["…"]`) must exist in its catalog, or the build fails. This is what catches a forgotten/typo `ResourceSource` (which would otherwise fall back silently).
- Build validators/handlers with a real localizer from the embedded catalog via `SharedResourcesLocalizerFactory.Create()` (test-project `Support/` helper), not a stub.

## Emails / background handlers

Integration-event handlers run without an HTTP request, so there is no negotiated culture. Localizing outbound emails needs the recipient's stored locale propagated to the handler — **not yet implemented** (tracked for a future PR); email bodies stay English for now.
