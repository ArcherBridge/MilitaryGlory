using TaleWorlds.SaveSystem;

namespace MilitaryGlory
{
    public sealed class MilitaryGlorySaveDefiner : SaveableTypeDefiner
    {
        public MilitaryGlorySaveDefiner() : base(41023) { }
        protected override void DefineClassTypes() => AddClassDefinition(typeof(SoldierRecord), 1);
        protected override void DefineContainerDefinitions()
        {
            ConstructContainerDefinition(typeof(System.Collections.Generic.List<SoldierRecord>));
            ConstructContainerDefinition(typeof(System.Collections.Generic.Dictionary<string, string>));
        }
    }
}
