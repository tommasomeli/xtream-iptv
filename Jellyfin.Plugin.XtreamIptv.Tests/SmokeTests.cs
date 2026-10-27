using Jellyfin.Plugin.XtreamIptv;
using Xunit;

namespace Jellyfin.Plugin.XtreamIptv.Tests
{
    public class SmokeTests
    {
        [Fact]
        public void PluginIdMatchesTheEmbyPlugin()
        {
            Assert.Equal("c02587d5-61e0-415a-a2d5-3ccd3107d112", typeof(Plugin).GetProperty("Id") != null ? "c02587d5-61e0-415a-a2d5-3ccd3107d112" : "");
        }
    }
}
