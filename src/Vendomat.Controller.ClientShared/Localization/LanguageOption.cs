namespace Vendomat.Controller.Client.Localization;

public sealed class LanguageOption(string code, string nativeName, string flagImagePath, string countryCode)
{
    public LanguageOption(string code, string nativeName)
        : this(code, nativeName, ResolveFlagImagePath(code), ResolveCountryCode(code))
    {
    }

    public string Code { get; } = code;

    public string NativeName { get; } = nativeName;

    public string FlagImagePath { get; } = flagImagePath;

    public string CountryCode { get; } = countryCode;

    private static string ResolveFlagImagePath(string code) => code switch
    {
        "hu-HU" => "hu.svg",
        "en-US" => "us.svg",
        _ => "ro.svg",
    };

    private static string ResolveCountryCode(string code) => code switch
    {
        "hu-HU" => "HU",
        "en-US" => "US",
        _ => "RO",
    };
}
