namespace Factory.Plant.Service.Web;

public sealed class WebsiteSettings
{
    public bool Enabled { get; set; }
    public string PublicOrigin { get; set; } = "";
    public string DataProtectionBlobUri { get; set; } = "";
    public string DataProtectionKeyId { get; set; } = "";

    public IReadOnlyList<string> Validate(IHostEnvironment environment)
    {
        var errors = new List<string>();
        if (!HttpsUri(PublicOrigin, out var origin) || origin!.AbsolutePath != "/")
            errors.Add("Factory:Web:PublicOrigin (HTTPS origin without a path)");
        if (!environment.IsDevelopment())
        {
            if (!HttpsUri(DataProtectionBlobUri, out var blob) || blob!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2)
                errors.Add("Factory:Web:DataProtectionBlobUri");
            if (!HttpsUri(DataProtectionKeyId, out var key) ||
                key!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is not ["keys", { Length: > 0 }])
                errors.Add("Factory:Web:DataProtectionKeyId (versionless Key Vault key URI)");
        }
        return errors;
    }

    private static bool HttpsUri(string value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
