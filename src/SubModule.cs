using Bannerlord.UIExtenderEx;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace MilitaryGlory
{
    public sealed class SubModule : MBSubModuleBase
    {
        private UIExtender _uiExtender;
        private Harmony _harmony;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            _uiExtender = UIExtender.Create("MilitaryGlory");
            _uiExtender.Register(typeof(SubModule).Assembly);
            _uiExtender.Enable();
            _harmony = new Harmony("MilitaryGlory.AutoResolve");
            _harmony.PatchAll(typeof(SubModule).Assembly);
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (game.GameType is Campaign && gameStarterObject is CampaignGameStarter starter)
                starter.AddBehavior(new MilitaryGloryBehavior());
        }

        protected override void OnSubModuleUnloaded()
        {
            _uiExtender?.Disable();
            _harmony?.UnpatchAll("MilitaryGlory.AutoResolve");
            base.OnSubModuleUnloaded();
        }

    }
}
