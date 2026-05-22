using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.EndField;

[RipperHook(GameType.EndField)]
public sealed class EndField_Hook : RipperHookCommon
{
    protected override void InitAttributeHook()
    {
        RegisterModule(new GameBundleHook(EndFieldGameBundleBootstrap.PreInitialize));
        base.InitAttributeHook();
    }
}
