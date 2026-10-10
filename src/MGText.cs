using TaleWorlds.Localization;

namespace MilitaryGlory
{
    internal static class MGText
    {
        internal static string Get(string id, string english) =>
            new TextObject("{=" + id + "}" + english).ToString();
    }
}
