using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System.Collections;
using System.Text;
using System.Threading;
using UnityObjectCopy.Native;
using UnityEngine;

namespace UnityObjectCopy
{
    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "experimental.assassin1076.UnityObjectCopy";
        public const string PLUGIN_NAME = "UnityObjectCopy";
        public const string PLUGIN_VERSION = "0.0.1";
    }

    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;

        internal static ConfigEntry<string> InstantiateRvaOverride;
        internal static ConfigEntry<bool> PoolCloneContext;
        internal static ConfigEntry<bool> RunSelfTest;
        internal static ConfigEntry<bool> VerboseLogging;

        private void Awake()
        {
            Logger = base.Logger;

            InstantiateRvaOverride = Config.Bind("Native", "InstantiateObjectRva", string.Empty,
                "手动指定 UnityPlayer.dll 中 InstantiateObject 的 RVA（例如 0x44FE20）。");
            PoolCloneContext = Config.Bind("Native", "PoolCloneContext", true,
                "复用每个线程一份的原生调用上下文，避免每次复制都向 Unity 申请新内存。");
            RunSelfTest = Config.Bind("Diagnostics", "RunSelfTest", true,
                "启动后自动克隆一个测试物体并校验结果。");
            VerboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
                "输出每次原生调用的地址与上下文条目数。");

            InstantiateObjectBinding.SetMainThreadId(Thread.CurrentThread.ManagedThreadId);

            bool ready = InstantiateObjectBinding.Initialize(
                InstantiateRvaOverride.Value, PoolCloneContext.Value, VerboseLogging.Value);

            if (ready)
                Logger.LogInfo("[Native] " + InstantiateObjectBinding.Report);
            else
                Logger.LogError("[Native] InstantiateObject 绑定失败: " + InstantiateObjectBinding.Report);

            Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

            if (ready && RunSelfTest.Value)
                StartCoroutine(SelfTestRoutine());
        }

        private void OnDestroy()
        {
            InstantiateObjectBinding.ReleasePooledContext();
        }

        private IEnumerator SelfTestRoutine()
        {
            yield return null;
            yield return null;

            var report = new StringBuilder();
            int failures = 0;

            GameObject source = null;
            GameObject clone = null;
            int awakesBeforeClone = 0;
            bool cloned = false;

            try
            {
                source = new GameObject("UOC_SelfTest_Source");
                source.transform.position = new Vector3(12.5f, 3.25f, -7.75f);
                source.transform.rotation = Quaternion.Euler(10f, 20f, 30f);
                GameObject child = new GameObject("UOC_SelfTest_Child");
                child.transform.SetParent(source.transform, false);
                source.AddComponent<SelfTestMarker>();

                awakesBeforeClone = SelfTestMarker.AwakeCount;
                clone = AdditionalCopyAPI.CloneWithoutActivation(source) as GameObject;
                cloned = true;
            }
            catch (System.Exception e)
            {
                failures++;
                report.AppendLine("  克隆阶段异常: " + e);
            }

            if (cloned)
            {
                // 再等一帧，留给引擎“补跑 Awake”的机会，这样 activeSelf 检查才有意义。
                yield return null;

                try
                {
                    failures += Check(report, clone != null, "克隆结果非 null");
                    if (clone != null)
                    {
                        failures += Check(report, clone.GetInstanceID() != source.GetInstanceID(), "克隆体是独立对象");
                        failures += Check(report, clone.name == source.name + "(Clone)", "名称后缀为 (Clone): " + clone.name);
                        failures += Check(report,
                            Vector3.Distance(clone.transform.position, source.transform.position) < 1e-4f,
                            "世界位置一致: " + clone.transform.position);
                        failures += Check(report,
                            Quaternion.Angle(clone.transform.rotation, source.transform.rotation) < 1e-3f,
                            "世界旋转一致");
                        failures += Check(report, clone.transform.childCount == 1, "子物体被整体复制");
                        failures += Check(report, !clone.activeSelf, "克隆体处于未激活状态");
                        failures += Check(report, SelfTestMarker.AwakeCount == awakesBeforeClone,
                            "克隆体没有触发 Awake (awake 次数 " + SelfTestMarker.AwakeCount + ")");
                    }
                }
                catch (System.Exception e)
                {
                    failures++;
                    report.AppendLine("  校验阶段异常: " + e);
                }
            }

            if (clone != null)
                Destroy(clone);
            if (source != null)
                Destroy(source);

            if (failures == 0)
                Logger.LogInfo("[SelfTest] 全部通过\n" + report);
            else
                Logger.LogError("[SelfTest] " + failures + " 项失败\n" + report);
        }

        private static int Check(StringBuilder report, bool condition, string description)
        {
            report.AppendLine((condition ? "  [PASS] " : "  [FAIL] ") + description);
            return condition ? 0 : 1;
        }
    }

    /// <summary>
    /// 自检用的标记组件：只要 Awake 被调用就记录一次，用来证明拷贝过程没有激活克隆体。
    /// </summary>
    public class SelfTestMarker : MonoBehaviour
    {
        internal static int AwakeCount;

        private void Awake()
        {
            AwakeCount++;
        }
    }
}
