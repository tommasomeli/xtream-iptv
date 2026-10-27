using System;
using XtreamIptv;
using XtreamIptv.Catalog;
using XtreamIptv.LiveTv;
using XtreamIptv.Logging;
using System.Linq;
using System.Reflection;
using MediaBrowser.Controller.Entities;

namespace Emby.XtreamIptv.Catalog
{
    /// <summary>How one link (episode to season, item to series) is applied on the running server.</summary>
    internal sealed class LinkMember
    {
        /// <summary>True for a <c>SetXxxItem(BaseItem)</c> method, false for a settable long id property.</summary>
        public bool IsMethod { get; set; }
        public string Name { get; set; }
        public override string ToString() => (IsMethod ? "method " : "property ") + Name;
    }

    /// <summary>
    /// The season and series links differ between the SDK the plugin is compiled against (4.9:
    /// settable <c>AlbumId</c> / <c>SeriesId</c>) and the 4.10 runtime (read-only <c>AlbumId</c>,
    /// settable <c>InternalAlbumId</c>, methods <c>SetAlbumItem</c> / <c>SetSeriesItem</c>). A member
    /// that is not in both DLLs is never referenced from C#; it is resolved once by reflection at
    /// runtime, methods first, and cached as a delegate.
    /// </summary>
    internal static class EmbyCompat
    {
        private static readonly string[] AlbumProperties = { "InternalAlbumId", "AlbumId" };
        private static readonly string[] SeriesProperties = { "SeriesId", "InternalSeriesId" };

        private static readonly Lazy<Action<BaseItem, BaseItem>> AlbumLink = new Lazy<Action<BaseItem, BaseItem>>(
            () => Build(typeof(BaseItem), AlbumMember));
        private static readonly Lazy<Action<BaseItem, BaseItem>> SeriesLink = new Lazy<Action<BaseItem, BaseItem>>(
            () => Build(typeof(BaseItem), SeriesMember));

        private static readonly Lazy<LinkMember> LazyAlbumMember = new Lazy<LinkMember>(() => ResolveAlbumLink(typeof(BaseItem)));
        private static readonly Lazy<LinkMember> LazySeriesMember = new Lazy<LinkMember>(() => ResolveSeriesLink(typeof(BaseItem)));

        /// <summary>The member used for the episode to season link on this server (for logs and tests).</summary>
        public static LinkMember AlbumMember => LazyAlbumMember.Value;

        /// <summary>The member used for the item to series link on this server.</summary>
        public static LinkMember SeriesMember => LazySeriesMember.Value;

        /// <summary>Links an episode to its season (what <c>Episode.Season</c> reads).</summary>
        public static void LinkToSeason(BaseItem episode, BaseItem season) => AlbumLink.Value(episode, season);

        /// <summary>Links a season or episode to its series (what <c>Episode.Series</c> and Next Up read).</summary>
        public static void LinkToSeries(BaseItem item, BaseItem series) => SeriesLink.Value(item, series);

        /// <summary>SetAlbumItem(BaseItem) when the type has it, else the first settable long property of the album list.</summary>
        public static LinkMember ResolveAlbumLink(Type baseItemType) => Resolve(baseItemType, "SetAlbumItem", AlbumProperties);

        /// <summary>SetSeriesItem(BaseItem) when the type has it, else the first settable long property of the series list.</summary>
        public static LinkMember ResolveSeriesLink(Type baseItemType) => Resolve(baseItemType, "SetSeriesItem", SeriesProperties);

        /// <summary>Works on any loaded BaseItem type (also one loaded from another SDK for a test), so members are matched by name.</summary>
        internal static LinkMember Resolve(Type baseItemType, string methodName, string[] propertyNames)
        {
            var method = FindMethod(baseItemType, methodName);
            if (method != null) return new LinkMember { IsMethod = true, Name = methodName };
            foreach (var name in propertyNames)
            {
                if (FindSetter(baseItemType, name) != null) return new LinkMember { IsMethod = false, Name = name };
            }
            throw new MissingMemberException(baseItemType.FullName, methodName + " or " + string.Join("/", propertyNames));
        }

        private static Action<BaseItem, BaseItem> Build(Type baseItemType, LinkMember member)
        {
            if (member.IsMethod)
            {
                var method = FindMethod(baseItemType, member.Name);
                var call = (Action<BaseItem, BaseItem>)Delegate.CreateDelegate(typeof(Action<BaseItem, BaseItem>), method);
                return call;
            }
            var setter = FindSetter(baseItemType, member.Name);
            var set = (Action<BaseItem, long>)Delegate.CreateDelegate(typeof(Action<BaseItem, long>), setter);
            return (item, target) => set(item, target == null ? 0 : target.InternalId);
        }

        private static MethodInfo FindMethod(Type type, string name)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == name
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.Name == "BaseItem"
                    && m.ReturnType == typeof(void));
        }

        private static MethodInfo FindSetter(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || property.PropertyType != typeof(long)) return null;
            var setter = property.GetSetMethod(false);
            return setter;
        }
    }
}
