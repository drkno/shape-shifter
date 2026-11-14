using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ShapeShifter.Configuration;

public enum IdentityMode
{
    /// <summary>A new server identity next to the real PMS, for testing.</summary>
    SideBySide,

    /// <summary>The imported identity of the old PMS, which must be stopped.</summary>
    Takeover,
}

/// <summary>PMS's secureConnections preference (0 = required, 1 = preferred, 2 = disabled).</summary>
public enum SecureConnections
{
    Required = 0,
    Preferred = 1,
    Disabled = 2,
}

public class PluginConfiguration : BasePluginConfiguration
{
    public IdentityMode Mode { get; set; } = IdentityMode.SideBySide;

    /// <summary>Gets or sets the address the Plex listener binds to.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Gets or sets the listener port (HTTP and HTTPS share it, like PMS). 0 disables.</summary>
    public int HttpPort { get; set; } = 32400;

    /// <summary>Gets or sets an optional extra TLS-only port. 0 disables.</summary>
    public int HttpsPort { get; set; }

    /// <summary>Gets or sets the plex.direct PKCS#12 certificate (imported PMS cert-v2.p12).</summary>
    public string CertificatePath { get; set; } = string.Empty;

    public string CertificatePassword { get; set; } = string.Empty;

    /// <summary>Gets or sets a custom PKCS#12 certificate for custom access URLs.</summary>
    public string CustomCertificatePath { get; set; } = string.Empty;

    public string CustomCertificatePassword { get; set; } = string.Empty;

    /// <summary>Gets or sets the domain the custom certificate is for (PMS customCertificateDomain).</summary>
    public string CustomCertificateDomain { get; set; } = string.Empty;

    public SecureConnections SecureConnections { get; set; } = SecureConnections.Preferred;

    // Identity. Generated for side-by-side; imported for takeover.
    public string MachineIdentifier { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = "Shape-Shifter";

    /// <summary>Gets or sets the plex.tv server token (claim result, or PMS PlexOnlineToken).</summary>
    public string ServerToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the plex.tv account id of the server owner. Resolved from <see cref="ServerToken"/> if empty.</summary>
    public string OwnerPlexAccountId { get; set; } = string.Empty;

    public string OwnerPlexUsername { get; set; } = string.Empty;

    /// <summary>Gets or sets the PMS version advertised to clients.</summary>
    public string AdvertisedVersion { get; set; } = "1.41.3.9314-a0bfb8370";

    public bool EnableGdm { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether tokens are validated against plex.tv.</summary>
    public bool EnablePlexTvAuth { get; set; } = true;

    // Publishing to plex.tv.

    /// <summary>Gets or sets a value indicating whether connections are published to plex.tv.</summary>
    public bool PublishConnections { get; set; }

    /// <summary>Gets or sets LAN addresses to publish. Empty means detect from network interfaces.</summary>
    public string[] LanAddresses { get; set; } = [];

    /// <summary>Gets or sets the public address. Empty means ask plex.tv.</summary>
    public string PublicAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets the externally mapped port (PMS ManualPortMappingPort). 0 means no public connection.</summary>
    public int PublicPort { get; set; }

    /// <summary>Gets or sets custom server access URLs (PMS customConnections), published verbatim.</summary>
    public string[] CustomConnections { get; set; } = [];

    /// <summary>Gets or sets the Jellyfin URL used for loopback requests (images, HLS).</summary>
    public string JellyfinBaseUrl { get; set; } = "http://127.0.0.1:8096";

    /// <summary>Gets or sets the Jellyfin API key used for loopback calls. Created automatically if empty.</summary>
    public string JellyfinApiKey { get; set; } = string.Empty;

    // Metadata editing.

    /// <summary>Gets or sets users allowed to edit metadata besides Jellyfin admins: Jellyfin user ids, Plex account ids or Plex usernames.</summary>
    public string[] MetadataEditors { get; set; } = [];

    public bool AllowMediaDeletion { get; set; }

    // Import.

    /// <summary>Gets or sets the PMS data directory (contains Preferences.xml).</summary>
    public string PmsDataPath { get; set; } = string.Empty;

    /// <summary>Gets or sets path rewrites applied to PMS file paths during import, one "from=>to" per entry.</summary>
    public string[] PathRewrites { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the import also takes over PMS's identity and certificates.</summary>
    public bool ImportIdentity { get; set; }

    public UserMapping[] UserMappings { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether users this server is shared with on plex.tv
    /// (Manage Library Access) get a Jellyfin user automatically, with library access following
    /// the share. They sign in through Plex, or PlexSSO (which links by Plex username).
    /// </summary>
    public bool AutoCreateUsers { get; set; } = true;

    /// <summary>Gets or sets the plex.tv account ids whose Jellyfin users were created for a share, and so are kept in step with it.</summary>
    public string[] ManagedPlexAccountIds { get; set; } = [];
}

public class UserMapping
{
    /// <summary>Gets or sets the plex.tv numeric account id.</summary>
    public string PlexAccountId { get; set; } = string.Empty;

    public string PlexUsername { get; set; } = string.Empty;

    public string JellyfinUserId { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional static token accepted for this user without asking plex.tv (testing, offline).</summary>
    public string LocalToken { get; set; } = string.Empty;
}
