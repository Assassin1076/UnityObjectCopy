using System;
using UnityObjectCopy.Native;
using UnityEngine;

namespace UnityObjectCopy
{
    /// <summary>
    /// 给 mod 作者使用的“纯拷贝、不激活”物体复制 API。
    ///
    /// 语义说明：
    /// <list type="bullet">
    /// <item>直接调用 UnityPlayer 内部的 <c>InstantiateObject</c>，它只负责克隆 + 加 "(Clone)" 后缀 +
    /// 设置位置/旋转，<b>不会</b>触发 Awake / OnEnable；</item>
    /// <item>默认还会把克隆体的 activeSelf 置为 false，保证后续帧里 Unity 也不会补跑 Awake，
    /// 想完全保留原始 activeSelf 请传 <c>deactivate: false</c>（此时引擎会在本帧末尾正常唤醒它）。</item>
    /// </list>
    /// </summary>
    public static class AdditionalCopyAPI
    {
        /// <summary>UnityPlayer 版本受支持且特征码校验通过。</summary>
        public static bool IsAvailable
        {
            get { return InstantiateObjectBinding.IsReady; }
        }

        /// <summary>Debug用。</summary>
        public static string Diagnostics
        {
            get { return InstantiateObjectBinding.Report; }
        }

        /// <summary>
        /// 复制一个物体但不激活它，位置/旋转沿用原物体当前的世界变换。
        /// </summary>
        public static UnityEngine.Object CloneWithoutActivation(UnityEngine.Object original)
        {
            return CloneWithoutActivation(original, true);
        }

        /// <summary>
        /// 复制一个物体但不激活它。
        /// </summary>
        /// <param name="deactivate">为 true 时把克隆体 activeSelf 置为 false（推荐，可确保不触发 Awake）。</param>
        public static UnityEngine.Object CloneWithoutActivation(UnityEngine.Object original, bool deactivate)
        {
            Vector3 position;
            Quaternion rotation;
            GetCurrentPose(original, out position, out rotation);
            return CloneWithoutActivation(original, position, rotation, deactivate, null);
        }

        /// <summary>
        /// 复制一个物体但不激活它，并显式指定世界位置/旋转。
        /// </summary>
        public static UnityEngine.Object CloneWithoutActivation(UnityEngine.Object original, Vector3 position,
                                                                Quaternion rotation, bool deactivate = true)
        {
            return CloneWithoutActivation(original, position, rotation, deactivate, null);
        }

        /// <summary>
        /// 复制一个物体但不激活它，并显式指定世界位置/旋转与父物体。
        /// </summary>
        /// <param name="parent">
        /// 传给原生 <c>InstantiateObject</c> 的父物体参数。Unity 自己的调用方总是传 null，
        /// 因此非 null 的用法属于实验特性，请在目标版本上自行验证。
        /// </param>
        public static UnityEngine.Object CloneWithoutActivation(UnityEngine.Object original, Vector3 position,
                                                               Quaternion rotation, bool deactivate,
                                                               Transform parent)
        {
            if (!InstantiateObjectBinding.IsReady)
                throw new InvalidOperationException("InstantiateObject 绑定不可用: " + InstantiateObjectBinding.Report);

            if (original == null)
                throw new ArgumentNullException("original");

            IntPtr source = InstantiateObjectBinding.GetNativePointer(original);
            if (source == IntPtr.Zero)
                throw new ArgumentException("原始对象没有原生实例（已被销毁，或者根本不是 UnityEngine.Object）", "original");

            IntPtr parentPtr = parent == null ? IntPtr.Zero : InstantiateObjectBinding.GetNativePointer(parent);

            IntPtr clone = InstantiateObjectBinding.CloneNative(source, parentPtr, ref position, ref rotation);
            if (clone == IntPtr.Zero)
                return null;

            UnityEngine.Object result = InstantiateObjectBinding.WrapNative(clone);
            if (deactivate)
                Deactivate(result);

            return result;
        }

        /// <summary>泛型版本；返回的托管包装类型由原生 classID 决定，若与 T 不符则返回 null。</summary>
        public static T CloneWithoutActivation<T>(T original, bool deactivate = true) where T : UnityEngine.Object
        {
            return CloneWithoutActivation((UnityEngine.Object)original, deactivate) as T;
        }

        /// <summary>泛型版本；返回的托管包装类型由原生 classID 决定，若与 T 不符则返回 null。</summary>
        public static T CloneWithoutActivation<T>(T original, Vector3 position, Quaternion rotation,
                                                  bool deactivate = true) where T : UnityEngine.Object
        {
            return CloneWithoutActivation(original, position, rotation, deactivate, null) as T;
        }

        private static void Deactivate(UnityEngine.Object clone)
        {
            if (clone == null)
                return;

            GameObject gameObject = clone as GameObject;
            if (gameObject == null)
            {
                Component component = clone as Component;
                if (component != null)
                    gameObject = component.gameObject;
            }

            if (gameObject != null && gameObject.activeSelf)
                gameObject.SetActive(false);
        }

        private static void GetCurrentPose(UnityEngine.Object original, out Vector3 position, out Quaternion rotation)
        {
            Transform transform = null;

            GameObject gameObject = original as GameObject;
            if (gameObject != null)
                transform = gameObject.transform;
            else
            {
                Component component = original as Component;
                if (component != null)
                    transform = component.transform;
            }

            if (transform != null)
            {
                position = transform.position;
                rotation = transform.rotation;
            }
            else
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
            }
        }
    }
}
