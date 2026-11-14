using System.Globalization;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ShapeShifter;

/// <summary>
/// Makes Jellyfin answer to Plex clients as if it were a Plex Media Server.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginName = "Shape-Shifter";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        EnsureIdentity();
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => PluginName;

    public override Guid Id => Guid.Parse("5c1a3e7b-9f0d-4b8e-a6c2-0d3f4e5a6b71");

    public override string Description => "Impersonates a Plex Media Server so unmodified Plex clients can use Jellyfin.";

    /// <summary>
    /// Settings saved from the dashboard can't blank values the plugin set itself (the claimed
    /// server token, the resolved owner, the loopback API key) for the same identity. Those
    /// change only through Claim, import or NewIdentity.
    /// </summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration incoming && incoming.MachineIdentifier == Configuration.MachineIdentifier)
        {
            KeepIfBlanked(incoming, c => c.ServerToken, (c, v) => c.ServerToken = v);
            KeepIfBlanked(incoming, c => c.OwnerPlexAccountId, (c, v) => c.OwnerPlexAccountId = v);
            KeepIfBlanked(incoming, c => c.JellyfinApiKey, (c, v) => c.JellyfinApiKey = v);
        }

        base.UpdateConfiguration(configuration);
    }

    private void KeepIfBlanked(PluginConfiguration incoming, Func<PluginConfiguration, string> get, Action<PluginConfiguration, string> set)
    {
        if (string.IsNullOrEmpty(get(incoming)) && !string.IsNullOrEmpty(get(Configuration)))
        {
            set(incoming, get(Configuration));
        }
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
        };
    }

    private void EnsureIdentity()
    {
        if (!string.IsNullOrEmpty(Configuration.MachineIdentifier))
        {
            return;
        }

        Configuration.MachineIdentifier = NewMachineIdentifier();
        SaveConfiguration();
    }

    /// <summary>PMS machine identifiers (ProcessedMachineIdentifier) are 40 lowercase hex chars.</summary>
    internal static string NewMachineIdentifier() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
}
