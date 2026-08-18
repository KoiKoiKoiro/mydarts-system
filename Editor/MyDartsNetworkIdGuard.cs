#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDKBase;

namespace MyDartsSystem.EditorTools
{
    /// <summary>
    /// 再生する直前に、消えたスクリプトのネットワーク記録を片づける。
    ///
    /// VRChatは「どのオブジェクトが何番の通信枠を使うか」をシーンに記録している。
    /// スクリプトを消しても記録だけが残るので、次に再生したときに
    ///
    ///     InvalidObject at 348: [VRC.Udon.UdonBehaviour]
    ///     Found 9 errors while configuring network IDs!
    ///
    /// と言われて再生が止まる。SDK側の条件はこれ。
    ///
    ///     if (!pair.gameObject) → InvalidObject
    ///
    /// 消すのは「実物が無くなった記録」だけなので、
    /// 生きているオブジェクトの番号は動かない。手で押す必要もない。
    /// </summary>
    [InitializeOnLoad]
    public static class MyDartsNetworkIdGuard
    {
        static MyDartsNetworkIdGuard()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            // 再生に入る直前だけ見る
            if (state != PlayModeStateChange.ExitingEditMode) return;
            Clean();
        }

        private static void Clean()
        {
            var descriptor = Object.FindObjectOfType<VRC_SceneDescriptor>(true);
            if (descriptor == null) return;
            if (descriptor.NetworkIDCollection == null) return;

            var before = descriptor.NetworkIDCollection.Count;
            descriptor.NetworkIDCollection.RemoveAll(p => p == null || p.gameObject == null);
            var removed = before - descriptor.NetworkIDCollection.Count;

            if (removed <= 0) return;

            EditorUtility.SetDirty(descriptor);
            PrefabUtility.RecordPrefabInstancePropertyModifications(descriptor);
            EditorSceneManager.MarkSceneDirty(descriptor.gameObject.scene);

            Debug.Log("[MyDarts] 消えたスクリプトのネットワーク記録を " + removed +
                      " 件 片づけました。（再生前の自動処理）");
        }
    }
}
#endif
