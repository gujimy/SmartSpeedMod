using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;

// 程序集级清单：CesiumLoader 的 mod 列表从这里读取名称/版本/权限。
// 版本需与 SmartSpeedMod.json 的 version 保持一致。
[assembly: ModManifest(
    "智能倍速",
    version: "1.0.0",
    author: "喵喵酱",
    description: "局内智能加速，自己抉择时恢复 1x，支持吉星小镇",
    Permissions = ModPermission.ReadGameState | ModPermission.SpeedHack,
    SdkVersion = "2.8.6",
    Platforms = ModPlatform.Windows)]

namespace SmartSpeedMod
{
    /// <summary>Mod 入口：CesiumLoader 通过 Main() 启动</summary>
    public static class ModEntry
    {
        public static void Main()
        {
            // 延迟 6 秒，等待 NetManager / UIManager 初始化完成后再挂钩事件
            ModBase.Run<SmartSpeedController>(new SmartSpeedController(), 6000, "SMARTSPEED");
        }
    }
}
