using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace UnityObjectCopy.Native
{
    /// <summary>
    /// UnityPlayer 内部 <c>InstantiateObject</c> 的薄绑定。
    ///
    /// 目标函数（x64 fastcall，反编译签名）：
    /// <code>
    /// __int64 __fastcall InstantiateObject(__int64 originalObject,
    ///                                      __int64 parentTransform,
    ///                                      const Vector3f* position,
    ///                                      const Quaternionf* rotation,
    ///                                      CloneContext* context);
    /// </code>
    ///
    /// 该函数负责复制和位置设置，在原版提供的C# API中调用的是该函数的一个包装，该包装执行这个函数后调用 <c>AwakeAndActivateClonedObjects</c> 激活克隆体；
    /// 因此尝试构造这个函数的C# wrapper就可以获得一个不激活拷贝API
    ///
    /// 上下文结构（0x60 字节，调用方提供；内部只用其中三个字段做动态数组）：
    /// <code>
    /// +0x38  void*  allocatorHandle   // 不透明，入口必须为 0
    /// +0x48  void*  objectsBegin      // 动态数组起始
    /// +0x50  void*  objectsEnd        // 动态数组结束
    /// +0x58  void*  objectsCapacity   // 动态数组容量
    /// </code>
    /// 本绑定跳过激活，因此刻意不释放该数组（见 <see cref="PoolCloneContext"/> 说明，泄漏被复用池限制为每线程一份）。
    /// </summary>
    internal static unsafe class InstantiateObjectBinding
    {

        // 版本表、特征码与版本匹配规则都集中在 TargetProfile
        private const string SignatureText = TargetProfile.InstantiateObjectSignature;

        // ---- 原生结构 / ABI 常量 --------------------------------------------------------
        internal const int CloneContextSize = 0x60;
        private const int ContextAllocatorHandleOffset = 0x38;
        private const int ContextObjectsBeginOffset = 0x48;
        private const int ContextObjectsEndOffset = 0x50;
        private const int ContextObjectsCapacityOffset = 0x58;

        /// <summary>UnityEngine.Object 原生头中 m_InstanceID 的偏移（vtable 之后，classID 之前）。</summary>
        private const int NativeInstanceIdOffset = 0x8;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr InstantiateObjectFn(IntPtr originalObject, IntPtr parentTransform,
                                                    Vector3* position, Quaternion* rotation, IntPtr context);

        // ---- 状态 ----------------------------------------------------------------------
        private static InstantiateObjectFn _instantiate;
        private static bool _poolContext = true;
        private static bool _verbose;
        private static string _report = "尚未初始化";
        private static int _mainThreadId;

        [ThreadStatic] private static IntPtr _threadContext;

        private static FieldInfo _cachedPtrField;
        private static MethodInfo _findObjectFromInstanceId;
        private static MethodInfo _forceLoadFromInstanceId;

        internal static bool IsReady { get { return _instantiate != null; } }
        internal static string Report { get { return _report; } }
        internal static int MainThreadId { get { return _mainThreadId; } }

        internal static void SetMainThreadId(int threadId)
        {
            _mainThreadId = threadId;
        }

        internal static bool Initialize(string rvaOverrideText, bool poolContext, bool verbose)
        {
            _poolContext = poolContext;
            _verbose = verbose;

            try
            {
                ResolveMembers();

                UnityPlayerImage image = UnityPlayerImage.TryLoad();
                if (image == null)
                {
                    _report = "未找到已加载的 UnityPlayer.dll（不是 Unity 进程？）";
                    return false;
                }

                byte[] pattern;
                byte[] mask;
                string error;
                if (!PatternScanner.TryParse(SignatureText, out pattern, out mask, out error))
                {
                    _report = "特征码解析失败: " + error;
                    return false;
                }

                IntPtr target;
                string how;
                if (!TryResolveByOverride(image, rvaOverrideText, pattern, mask, out target, out how))
                {
                    if (!string.IsNullOrWhiteSpace(rvaOverrideText) && !string.IsNullOrEmpty(how))
                        Plugin.Logger.LogWarning("[Native] " + how);

                    if (!TryResolveByKnownBuild(image, pattern, mask, out target, out how) &&
                        !TryResolveBySignature(image, pattern, mask, out target, out how))
                    {
                        _report = how;
                        return false;
                    }
                }

                _instantiate = (InstantiateObjectFn)Marshal.GetDelegateForFunctionPointer(target, typeof(InstantiateObjectFn));

                string version = string.IsNullOrEmpty(image.ProductVersion) ? image.FileVersion : image.ProductVersion;
                _report = string.Format(CultureInfo.InvariantCulture,
                    "InstantiateObject = 0x{0:X} (RVA 0x{1:X}, {2}), UnityPlayer {3}, 上下文池={4}",
                    target.ToInt64(), target.ToInt64() - image.BaseAddress.ToInt64(), how, version, _poolContext);
                return true;
            }
            catch (Exception e)
            {
                _instantiate = null;
                _report = "初始化异常: " + e;
                return false;
            }
        }

        private static void ResolveMembers()
        {
            const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            _cachedPtrField = typeof(UnityEngine.Object).GetField("m_CachedPtr", InstanceFlags);

            // 两种把 instanceID 变成托管包装的入口，优先用 internal 的。
            Type objectType = typeof(UnityEngine.Object);
            _findObjectFromInstanceId = objectType.GetMethod("FindObjectFromInstanceID", StaticFlags, null,
                new[] { typeof(int) }, null);
            _forceLoadFromInstanceId = objectType.GetMethod("ForceLoadFromInstanceID", StaticFlags, null,
                new[] { typeof(int) }, null);

            if (_cachedPtrField == null)
                throw new MissingFieldException("UnityEngine.Object.m_CachedPtr 不存在，无法取得原生指针");
        }

        private static bool TryResolveByOverride(UnityPlayerImage image, string text, byte[] pattern, byte[] mask,
                                                 out IntPtr target, out string how)
        {
            target = IntPtr.Zero;
            how = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string trimmed = text.Trim();
            long value;
            bool parsed;
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                parsed = long.TryParse(trimmed.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            else
                parsed = long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

            if (!parsed || value <= 0)
            {
                how = "配置项 Native/InstantiateObjectRva 无法解析: " + text;
                return false;
            }

            long imageBase = image.BaseAddress.ToInt64();
            // 大于 0x10000000 视为绝对地址，否则视为 RVA。
            target = new IntPtr(value > 0x10000000 ? value : imageBase + value);
            how = "配置覆盖";

            long delta = target.ToInt64() - imageBase;
            if (delta < 0 || delta > image.SizeOfImage)
            {
                how = "配置覆盖地址不在 UnityPlayer.dll 映射范围内: 0x" + target.ToInt64().ToString("X", CultureInfo.InvariantCulture);
                return false;
            }

            if (!PatternScanner.Matches(target, pattern, mask))
                Plugin.Logger.LogWarning("[Native] 覆盖地址处的特征码不匹配，按用户配置强行使用（风险自负）");

            return true;
        }

        private static bool TryResolveByKnownBuild(UnityPlayerImage image, byte[] pattern, byte[] mask,
                                                   out IntPtr target, out string how)
        {
            target = IntPtr.Zero;
            how = null;

            string product = image.ProductVersion ?? string.Empty;
            string file = image.FileVersion ?? string.Empty;

            foreach (KnownBuild build in TargetProfile.KnownBuilds)
            {
                if (!TargetProfile.MatchesVersion(product, file, build.Version))
                    continue;

                IntPtr candidate = image.RvaToPointer(build.InstantiateObjectRva);
                if (!PatternScanner.Matches(candidate, pattern, mask))
                {
                    how = string.Format(CultureInfo.InvariantCulture,
                        "版本表命中 {0}，但 RVA 0x{1:X} 处特征码不匹配（游戏可能被更新/重编译）",
                        build.Version, build.InstantiateObjectRva);
                    return false;
                }

                target = candidate;
                how = "版本表 " + build.Version;
                return true;
            }

            return false;
        }

        private static bool TryResolveBySignature(UnityPlayerImage image, byte[] pattern, byte[] mask,
                                                  out IntPtr target, out string how)
        {
            int hits;
            target = PatternScanner.Find(image, pattern, mask, out hits);
            if (target == IntPtr.Zero || hits != 1)
            {
                how = string.Format(CultureInfo.InvariantCulture,
                    "特征码扫描失败（命中 {0} 处），请在配置项 Native/InstantiateObjectRva 中手动指定 RVA", hits);
                target = IntPtr.Zero;
                return false;
            }

            how = "特征码扫描";
            return true;
        }

        // ---- 调用 ----------------------------------------------------------------------
        internal static IntPtr GetNativePointer(UnityEngine.Object obj)
        {
            if (obj == null)
                return IntPtr.Zero;
            return (IntPtr)_cachedPtrField.GetValue(obj);
        }

        /// <summary>
        /// 调用原生 InstantiateObject，返回克隆体的原生指针（失败返回 IntPtr.Zero）。
        /// </summary>
        internal static IntPtr CloneNative(IntPtr originalObject, IntPtr parentTransform,
                                           ref Vector3 position, ref Quaternion rotation)
        {
            if (_instantiate == null)
                throw new InvalidOperationException("InstantiateObject 绑定不可用: " + _report);

            if (originalObject == IntPtr.Zero)
                throw new ArgumentNullException("originalObject");

            EnsureMainThread();

            IntPtr context = AcquireContext();
            IntPtr clone;
            fixed (Vector3* pPosition = &position)
            fixed (Quaternion* pRotation = &rotation)
            {
                clone = _instantiate(originalObject, parentTransform, pPosition, pRotation, context);
            }

            if (_verbose)
            {
                Plugin.Logger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                    "[Native] InstantiateObject(0x{0:X}) -> 0x{1:X}, 上下文条目={2}",
                    originalObject.ToInt64(), clone.ToInt64(), CountContextEntries(context)));
            }

            if (!_poolContext)
                ReleaseContext(context);

            return clone;
        }

        /// <summary>
        /// 把原生 Object* 变成托管对象（必要时让脚本后端创建包装）。
        /// </summary>
        internal static UnityEngine.Object WrapNative(IntPtr nativeObject)
        {
            if (nativeObject == IntPtr.Zero)
                return null;

            int instanceId = *(int*)((byte*)nativeObject + NativeInstanceIdOffset);
            if (instanceId == 0)
                return null;

            // 把 instanceID 变回托管包装的几个入口，优先用会强制创建脚本对象的那个。
            UnityEngine.Object result = null;
            if (_forceLoadFromInstanceId != null)
                result = (UnityEngine.Object)_forceLoadFromInstanceId.Invoke(null, new object[] { instanceId });
            if (result == null && _findObjectFromInstanceId != null)
                result = (UnityEngine.Object)_findObjectFromInstanceId.Invoke(null, new object[] { instanceId });
            if (result == null)
                result = Resources.InstanceIDToObject(instanceId);

            if (result == null)
                Plugin.Logger.LogWarning("[Native] 无法为 instanceID " + instanceId + " 创建托管包装");

            return result;
        }

        private static void EnsureMainThread()
        {
            if (_mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new InvalidOperationException(
                    "InstantiateObject 只能在 Unity 主线程调用（当前线程 " + Thread.CurrentThread.ManagedThreadId +
                    "，主线程 " + _mainThreadId + "）");
            }
        }

        // ---- 上下文池 ------------------------------------------------------------------
        private static IntPtr AcquireContext()
        {
            if (!_poolContext)
                return AllocateZeroed();

            if (_threadContext == IntPtr.Zero)
            {
                _threadContext = AllocateZeroed();
                return _threadContext;
            }

            // 复用：保留动态数组的起始与容量，只把 end 拉回 begin，其余字段清零，
            // 语义等价于“全新的空上下文”，但不会每次都向 Unity 申请一块新内存。
            byte* context = (byte*)_threadContext;
            IntPtr begin = *(IntPtr*)(context + ContextObjectsBeginOffset);
            IntPtr capacity = *(IntPtr*)(context + ContextObjectsCapacityOffset);

            Zero((IntPtr)context, CloneContextSize);

            *(IntPtr*)(context + ContextObjectsBeginOffset) = begin;
            *(IntPtr*)(context + ContextObjectsEndOffset) = begin;
            *(IntPtr*)(context + ContextObjectsCapacityOffset) = capacity;
            *(IntPtr*)(context + ContextAllocatorHandleOffset) = IntPtr.Zero;
            return _threadContext;
        }

        private static void ReleaseContext(IntPtr context)
        {
            if (context == IntPtr.Zero)
                return;

            if (_threadContext == context)
                _threadContext = IntPtr.Zero;

            Marshal.FreeHGlobal(context);
        }

        /// <summary>释放当前线程缓存的上下文（插件卸载时调用；内部数组本身按设计不回收）。</summary>
        internal static void ReleasePooledContext()
        {
            if (_threadContext != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_threadContext);
                _threadContext = IntPtr.Zero;
            }
        }

        private static IntPtr AllocateZeroed()
        {
            IntPtr block = Marshal.AllocHGlobal(CloneContextSize);
            Zero(block, CloneContextSize);
            return block;
        }

        private static void Zero(IntPtr block, int size)
        {
            byte* p = (byte*)block;
            for (int i = 0; i < size; i++)
                p[i] = 0;
        }

        private static int CountContextEntries(IntPtr context)
        {
            if (context == IntPtr.Zero)
                return 0;
            byte* p = (byte*)context;
            long begin = (*(IntPtr*)(p + ContextObjectsBeginOffset)).ToInt64();
            long end = (*(IntPtr*)(p + ContextObjectsEndOffset)).ToInt64();
            return begin == 0 ? 0 : (int)((end - begin) / 8);
        }
    }
}
