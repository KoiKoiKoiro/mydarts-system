using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Persistence;

namespace MyDartsSystem
{
    /// <summary>
    /// ダーツギミック本体との橋渡し。
    ///
    /// 【設計方針】
    /// このスクリプトは相手のクラスを一切参照しない。
    /// 相手のスクリプトが PlayerData（パーシスタンス）に書いている値を、
    /// キー名だけを頼りに横から読む。
    ///
    /// こうしている理由は3つ:
    ///   1. 相手のファイルに触らずに済む（拡張として成立する）
    ///   2. VCCで配布したとき、ギミック本体を持っていない人でもコンパイルが通る
    ///   3. 相手がアップデートしても、キー名さえ変わらなければ壊れない
    ///
    /// キー名はInspectorから変更できる。相手側の仕様が変わったら、
    /// スクリプトを書き換えずにここを直すだけで追従できる。
    ///
    /// Crecent Darts (PublicVariant) の既定キー:
    ///   cd3_stats_01_games       01ゲームのプレイ数
    ///   cd3_stats_cricket_games  クリケットのプレイ数
    ///   cd3_stats_countup_games  カウントアップのプレイ数
    ///   cd3_stats_rating_x100    レーティング×100
    ///   cd3_stats_flight         フライト（ランク文字列）
    ///
    /// ※ ワールド内に出す文字列だけは、フォントの都合で英語にしてある。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsCrecentBridge : UdonSharpBehaviour
    {
        [Header("=== 監視するキー ===")]
        [SerializeField, Tooltip("01ゲームのプレイ数が入っているキー")]
        private string key01Games = "cd3_stats_01_games";

        [SerializeField, Tooltip("クリケットのプレイ数が入っているキー")]
        private string keyCricketGames = "cd3_stats_cricket_games";

        [SerializeField, Tooltip("カウントアップのプレイ数が入っているキー。報酬に含めないなら空欄")]
        private string keyCountUpGames = "cd3_stats_countup_games";

        [SerializeField, Tooltip("レーティング×100のキー。表示用")]
        private string keyRatingX100 = "cd3_stats_rating_x100";

        [SerializeField, Tooltip("フライト（ランク文字列）のキー。表示用")]
        private string keyFlight = "cd3_stats_flight";

        [Header("=== 報酬 ===")]
        [SerializeField, Range(0, 500), Tooltip("1ゲーム終わるごとに入るコイン")]
        private int coinsPerGame = 10;

        [SerializeField, Range(1f, 60f), Tooltip("プレイ数を見にいく間隔（秒）")]
        private float pollInterval = 5f;

        [Header("=== 通知先 ===")]
        [SerializeField, Tooltip("コインが発生したときに呼ぶ相手。空でも動く")]
        private UdonSharpBehaviour earnTarget;

        [SerializeField, Tooltip("上の相手に送るイベント名")]
        private string onEarnedEventName = "OnCoinsEarned";

        [SerializeField, Tooltip("開発用のログパネル")]
        private MyDartsDebugPanel debugPanel;

        // 前回見たときのプレイ数。ここを基準に差分を出す
        private const string KeySeenGames = "mydarts_games_seen";

        private bool _ready;
        private bool _polling;
        private int _pendingCoins;
        private int _lastTotal;

        // ============================================================
        //  他のスクリプトから読む用
        // ============================================================

        /// <summary>ダーツギミック本体の記録が見つかるか</summary>
        public bool IsHostPresent()
        {
            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return false;

            return HasKey(local, key01Games)
                || HasKey(local, keyCricketGames)
                || HasKey(local, keyCountUpGames);
        }

        /// <summary>報酬の対象になるプレイ数の合計</summary>
        public int GetTotalGames()
        {
            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return 0;

            return ReadInt(local, key01Games)
                 + ReadInt(local, keyCricketGames)
                 + ReadInt(local, keyCountUpGames);
        }

        public int GetRatingX100()
        {
            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return 0;
            return ReadInt(local, keyRatingX100);
        }

        public string GetFlight()
        {
            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return "";
            if (!HasKey(local, keyFlight)) return "";
            return PlayerData.GetString(local, keyFlight);
        }

        /// <summary>
        /// たまっているコインを受け取る。受け取ると0に戻る。
        /// 送信側は、送信が成功してからこれを呼ぶこと。
        /// </summary>
        public int PeekPendingCoins() { return _pendingCoins; }

        public int TakePendingCoins()
        {
            var v = _pendingCoins;
            _pendingCoins = 0;
            return v;
        }

        // ============================================================
        //  監視
        // ============================================================
        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player)) return;
            if (!player.isLocal) return;

            _ready = true;

            // 初回は基準を今の値に合わせる。
            // そうしないと、導入前に遊んだ分がまとめてコインになってしまう。
            var local = Networking.LocalPlayer;
            if (!PlayerData.HasKey(local, KeySeenGames))
            {
                _lastTotal = GetTotalGames();
                PlayerData.SetInt(KeySeenGames, _lastTotal);

                if (debugPanel != null)
                {
                    debugPanel.Log("bridge: baseline = " + _lastTotal);
                }
            }
            else
            {
                _lastTotal = PlayerData.GetInt(local, KeySeenGames);
            }

            if (debugPanel != null)
            {
                debugPanel.Log("bridge: host " + (IsHostPresent() ? "found" : "not found"));
            }

            StartPolling();
        }

        public void StartPolling()
        {
            if (_polling) return;
            _polling = true;
            SendCustomEventDelayedSeconds(nameof(Poll), pollInterval);
        }

        public void Poll()
        {
            if (!_ready)
            {
                SendCustomEventDelayedSeconds(nameof(Poll), pollInterval);
                return;
            }

            var total = GetTotalGames();

            if (total > _lastTotal)
            {
                var diff = total - _lastTotal;
                _lastTotal = total;
                PlayerData.SetInt(KeySeenGames, total);

                var gained = diff * coinsPerGame;
                _pendingCoins += gained;

                if (debugPanel != null)
                {
                    debugPanel.Log("bridge: +" + diff + " game(s) -> +" + gained + " coin(s)");
                }

                if (earnTarget != null && !string.IsNullOrEmpty(onEarnedEventName))
                {
                    earnTarget.SendCustomEvent(onEarnedEventName);
                }
            }

            SendCustomEventDelayedSeconds(nameof(Poll), pollInterval);
        }

        // ============================================================
        //  小物
        // ============================================================
        private bool HasKey(VRCPlayerApi player, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return PlayerData.HasKey(player, key);
        }

        private int ReadInt(VRCPlayerApi player, string key)
        {
            if (!HasKey(player, key)) return 0;
            return PlayerData.GetInt(player, key);
        }
    }
}
