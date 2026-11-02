using System;
using System.Collections.Generic;
using Jfresolve.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jfresolve;

public class JfresolvePlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public JfresolvePlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer
    )
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static JfresolvePlugin? Instance { get; private set; }

    public override string Name => "Jfresolve";
    public override Guid Id => Guid.Parse("506F18B8-5DAD-4CD3-B9A0-F7ED933E9939");
    public override string Description => "Virtual search results with on-demand streaming";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        yield return new PluginPageInfo
        {
            Name = "config",
            EmbeddedResourcePath = prefix + ".Config.config.html",
        };
    }
}
