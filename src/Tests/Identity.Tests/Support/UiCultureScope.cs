using System.Globalization;

namespace Identity.Tests.Support;

// Pins CurrentUICulture for one test: the catalog answers under the ambient culture, which is the
// developer machine's (pt-BR here, invariant in a Linux CI container) unless a test fixes it.
internal sealed class UiCultureScope : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

    public UiCultureScope(string culture)
    {
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
    }

    public void Dispose() => CultureInfo.CurrentUICulture = _previous;
}
