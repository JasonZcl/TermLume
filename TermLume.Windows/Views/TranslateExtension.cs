using System.Windows.Markup;
using TermLume.Shared.Services;

namespace TermLume.Windows.Views;

[MarkupExtensionReturnType(typeof(string))]
public sealed class TranslateExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Localization.Text(key);
}
