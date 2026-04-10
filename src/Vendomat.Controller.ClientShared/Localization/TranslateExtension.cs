using Microsoft.Maui.Controls.Xaml;

namespace Vendomat.Controller.Client.Localization;

[ContentProperty(nameof(Text))]
public sealed class TranslateExtension : IMarkupExtension<BindingBase>, IMarkupExtension
{
    public string Text { get; set; } = string.Empty;

    public string? StringFormat { get; set; }

    public BindingBase ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            return new Binding
            {
                Mode = BindingMode.OneTime,
                Source = string.Empty,
            };
        }

        var languageService = LanguageService.Current
            ?? throw new InvalidOperationException("LanguageService is not initialized.");

        return new Binding
        {
            Mode = BindingMode.OneWay,
            Path = $"Lang.{Text}",
            Source = languageService,
            StringFormat = StringFormat,
        };
    }

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
