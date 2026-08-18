using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace MyDartsSystem
{
    /// <summary>
    /// ホルダーを握ったら、自分の柄をダーツへ流す。
    ///
    /// ダーツギミック本体のホルダーは、握られたときに
    /// 「形」と「色」を自分で反映する。こちらはその横で「柄」を反映する。
    /// あちらのスクリプトには触らない。
    ///
    /// 握られたかどうかは IsHeld を見張って判断している。
    /// OnPickup が2つ目のスクリプトまで届く保証がないため、
    /// 確実に動くほうを選んだ。1オブジェクトを数回/秒 見るだけなので負荷はない。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsHolderHook : UdonSharpBehaviour
    {
        [Header("=== 参照 ===")]
        [SerializeField, Tooltip("自分の柄番号を持っているもの")]
        private MyDartsFetcher fetcher;

        [SerializeField, Tooltip("このホルダーが持っているダーツ全部")]
        private MyDartsSkin[] darts;

        [SerializeField, Tooltip("握られたかを見るためのピックアップ。空なら同じオブジェクトから探す")]
        private VRC_Pickup pickup;

        [SerializeField, Tooltip("開発用のログパネル")]
        private MyDartsDebugPanel debugPanel;

        [Header("=== 動作 ===")]
        [SerializeField, Range(0.1f, 1f), Tooltip("握られたかを見にいく間隔")]
        private float watchInterval = 0.25f;

        [SerializeField, Range(1, 30), Tooltip("オーナーが取れないときに諦めるまでの回数")]
        private int ownershipRetry = 10;

        private bool _wasHeld;
        private int _lastSent = -1;
        private int _tries;

        private void Start()
        {
            if (pickup == null)
            {
                pickup = (VRC_Pickup)GetComponent(typeof(VRC_Pickup));
            }

            // 繋ぎ忘れがあると黙って何も起きないので、起動時に状態を出しておく。
            // ログ用のパネル自体が繋がっていないこともあるので、直接も出す。
            var state = "skin hook: pickup=" + (pickup != null ? "ok" : "MISSING") +
                        " fetcher=" + (fetcher != null ? "ok" : "MISSING") +
                        " darts=" + (darts == null ? 0 : darts.Length);

            Debug.Log("[MyDarts] " + state);
            if (debugPanel != null) debugPanel.Log(state);

            SendCustomEventDelayedSeconds(nameof(Watch), 2f);
        }

        /// <summary>握られたかを見張る</summary>
        public void Watch()
        {
            SendCustomEventDelayedSeconds(nameof(Watch), watchInterval);

            if (pickup == null) return;

            var held = pickup.IsHeld;

            // 握った瞬間
            if (held && !_wasHeld)
            {
                if (debugPanel != null) debugPanel.Log("skin hook: holder grabbed");
                _tries = 0;
                ApplyNow();
            }

            // 握っている間に柄を変えたら、その場で反映する
            else if (held && CurrentPacked() != _lastSent)
            {
                _tries = 0;
                ApplyNow();
            }

            _wasHeld = held;
        }

        /// <summary>いまの柄をダーツへ流す</summary>
        public void ApplyNow()
        {
            if (fetcher == null)
            {
                Debug.LogError("[MyDarts] skin hook: fetcher が繋がっていません。" +
                               "「柄のセットアップ」で取り付け直してください。");
                if (debugPanel != null) debugPanel.Log("skin hook: fetcher が繋がっていません");
                return;
            }
            if (darts == null || darts.Length == 0)
            {
                Debug.LogError("[MyDarts] skin hook: ダーツが繋がっていません。" +
                               "「柄のセットアップ」で取り付け直してください。");
                if (debugPanel != null) debugPanel.Log("skin hook: ダーツが繋がっていません");
                return;
            }

            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;

            var tip = fetcher.GetTip();
            var barrel = fetcher.GetBarrel();
            var shaft = fetcher.GetShaft();
            var flight = fetcher.GetFlight();

            var anyNotOwned = false;

            for (var i = 0; i < darts.Length; i++)
            {
                var dart = darts[i];
                if (dart == null) continue;

                // 同期する値なので、書く前にオーナーを取る必要がある
                if (!Networking.IsOwner(local, dart.gameObject))
                {
                    Networking.SetOwner(local, dart.gameObject);
                    anyNotOwned = true;
                    continue;
                }

                dart.SetDesigns(tip, barrel, shaft, flight);
            }

            // オーナーの移動は1フレームで終わらないことがあるので、少し待って出直す
            _tries++;
            if (anyNotOwned && _tries < ownershipRetry)
            {
                SendCustomEventDelayedFrames(nameof(ApplyNow), 3);
                return;
            }

            _lastSent = CurrentPacked();

            if (debugPanel != null)
            {
                debugPanel.Log("skin: tip=" + tip + " barrel=" + barrel +
                               " shaft=" + shaft + " flight=" + flight);
            }
        }

        /// <summary>いまの柄を1つの数にまとめる。変化の検出だけに使う</summary>
        private int CurrentPacked()
        {
            if (fetcher == null) return -1;
            return (fetcher.GetTip() & 0xFF)
                 | ((fetcher.GetBarrel() & 0xFF) << 8)
                 | ((fetcher.GetShaft() & 0xFF) << 16)
                 | ((fetcher.GetFlight() & 0xFF) << 24);
        }
    }
}
