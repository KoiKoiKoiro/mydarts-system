using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components.Video;
using VRC.SDK3.Video.Components.Base;
using VRC.SDKBase;

namespace MyDartsSystem
{
    /// <summary>
    /// ガチャ演出の動画を回す係。ここは各自のローカルだけで動く。
    ///
    /// 動画は1本だけ用意して、その中の区間を行き来して使う。
    ///
    ///   0:00 - 0:12  画面の背景ループ（普段はここが流れている）
    ///   0:12 - 0:15  真っ黒
    ///   0:15 - 0:20  ガチャ導入
    ///   0:20 - 0:28  ガチャ待機ループ
    ///   0:30        コモン
    ///   0:40        レア
    ///   0:50        ベリーレア
    ///   1:00        ウルトラレア
    ///
    /// 動画プレイヤーは1つしか無いので、背景と演出は同じものを使い回す。
    /// 同時に見せる場面が無いので、これで足りる。
    ///
    /// 筐体の近くに人がいない間は止める。見ていないものを描き続けないため。
    ///
    /// 結果の区間は、途中から「保持ループ」に入って、
    /// 閉じるまでその場で回り続ける。次の区間へなだれ込ませないため。
    ///
    /// ※ この script は動画プレイヤーと同じ GameObject に置くこと。
    ///    再生の合図（OnVideoReady など）は同じ場所にしか届かない。
    ///
    /// ※ ワールド内に出す文字列だけは、フォントの都合で英語にしてある。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsVideo : UdonSharpBehaviour
    {
        // 状態
        public const int StateLoading = 0;   // 読み込み中
        public const int StateStandby = 1;   // 真っ黒で待機
        public const int StateIntro = 2;     // 導入を再生中
        public const int StateLoop = 3;      // 待機ループ中
        public const int StateResult = 4;    // 結果を再生中
        public const int StateFailed = 5;    // 読み込めなかった
        public const int StateBlocked = 6;   // 設定で止められている
        public const int StateBackground = 7; // 画面の背景として流している

        [Header("=== 動画 ===")]
        [SerializeField, Tooltip("同じ場所に付いている動画プレイヤー")]
        private BaseVRCVideoPlayer player;

        [SerializeField, Tooltip("動画のURL。直リンクのmp4を推奨")]
        private VRCUrl videoUrl;

        [Header("=== 区間（秒）===")]
        [SerializeField, Tooltip("画面の背景ループのはじまり")]
        private float backgroundStart = 0f;

        [SerializeField, Tooltip("画面の背景ループのおわり")]
        private float backgroundEnd = 12f;

        [SerializeField, Tooltip("待機する位置。真っ黒なところ")]
        private float standbyTime = 13f;

        [SerializeField, Tooltip("導入のはじまり")]
        private float introStart = 15f;

        [SerializeField, Tooltip("待機ループのはじまり")]
        private float loopStart = 20f;

        [SerializeField, Tooltip("待機ループのおわり。ここまで来たら頭へ戻す")]
        private float loopEnd = 28f;

        [SerializeField, Tooltip("結果のはじまり。順番は コモン / レア / ベリーレア / ウルトラレア")]
        private float[] resultStart = new float[] { 30f, 40f, 50f, 60f };

        [SerializeField, Tooltip("結果の保持ループのはじまり")]
        private float[] holdStart = new float[] { 32f, 42f, 52f, 62f };

        [SerializeField, Tooltip("結果の保持ループのおわり")]
        private float[] holdEnd = new float[] { 36f, 46f, 57f, 68f };

        [Header("=== 参照 ===")]
        [SerializeField, Tooltip("設定で止められている人に出す案内")]
        private GameObject blockedNotice;

        [SerializeField, Tooltip("演出で映す板。使っていない間は隠す")]
        private GameObject screenObject;

        [SerializeField, Tooltip("UIの後ろに敷く背景の板")]
        private GameObject backgroundScreen;

        [SerializeField] private MyDartsDebugPanel debugPanel;

        [Header("=== 近くにいる時だけ流す ===")]
        [SerializeField, Tooltip("筐体の位置")]
        private Transform cabinet;

        [SerializeField, Range(2f, 30f), Tooltip("これより遠い人には流さない（m）")]
        private float playRange = 8f;

        [Header("=== 読み直し ===")]
        [SerializeField, Range(5f, 30f), Tooltip("読み込みに失敗した時、次を試すまでの待ち（秒）")]
        private float retryDelay = 8f;

        [SerializeField, Range(0, 5), Tooltip("読み直す回数")]
        private int maxRetry = 3;

        private int _state = StateLoading;
        private int _tier;
        private int _tries;
        private bool _loadPending;
        private bool _nearby = true;
        private bool _bgPaused;

        private void Start()
        {
            if (blockedNotice != null) blockedNotice.SetActive(false);
            if (screenObject != null) screenObject.SetActive(false);
            if (backgroundScreen != null) backgroundScreen.SetActive(false);

            Load();
            SendCustomEventDelayedSeconds(nameof(CheckNearby), 1f);
        }

        /// <summary>
        /// 筐体の近くに人がいるかを、たまに見にいく。
        /// 誰も見ていないのに描き続けるのはもったいないので、離れたら止める。
        /// </summary>
        public void CheckNearby()
        {
            SendCustomEventDelayedSeconds(nameof(CheckNearby), 0.75f);

            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local) || cabinet == null) return;

            var near = Vector3.Distance(local.GetPosition(), cabinet.position) <= playRange;
            if (near == _nearby) return;

            _nearby = near;

            // 背景として流している時だけ効かせる。演出中に止まると台無しなので
            if (_state != StateBackground) return;
            ApplyBackgroundPlayback();
        }

        // ============================================================
        //  読み込み
        // ============================================================
        public void Load()
        {
            _loadPending = false;

            if (player == null)
            {
                Log("video: player missing");
                _state = StateFailed;
                return;
            }

            if (videoUrl == null || videoUrl.Get().Length == 0)
            {
                Log("video: url empty");
                _state = StateFailed;
                return;
            }

            _state = StateLoading;
            player.LoadURL(videoUrl);
            Log("video: loading");
        }

        public override void OnVideoReady()
        {
            _tries = 0;
            if (blockedNotice != null) blockedNotice.SetActive(false);

            Log("video: ready");
            PlayBackground();
        }

        public override void OnVideoError(VideoError videoError)
        {
            // 設定で止められている場合は、読み直しても無駄なので案内を出す
            if (videoError == VideoError.AccessDenied)
            {
                _state = StateBlocked;
                if (blockedNotice != null) blockedNotice.SetActive(true);
                Log("video: blocked (untrusted urls off)");
                return;
            }

            // 立て続けに読むと弾かれるので、間を空けて試し直す
            _tries++;
            if (_tries <= maxRetry)
            {
                Log("video: error " + videoError + " retry " + _tries);
                if (!_loadPending)
                {
                    _loadPending = true;
                    SendCustomEventDelayedSeconds(nameof(Load), retryDelay);
                }
                return;
            }

            _state = StateFailed;
            Log("video: gave up (" + videoError + ")");
        }

        // ============================================================
        //  進行
        // ============================================================

        /// <summary>導入から流す。ガチャを押した瞬間に呼ぶ</summary>
        public void PlayIntro()
        {
            if (!IsUsable()) return;

            if (screenObject != null) screenObject.SetActive(true);
            if (backgroundScreen != null) backgroundScreen.SetActive(false);

            player.SetTime(introStart);
            player.Play();
            _state = StateIntro;
        }

        /// <summary>レア度の区間へ飛ばす。0:コモン 1:レア 2:ベリーレア 3:ウルトラレア</summary>
        public void PlayResult(int tier)
        {
            if (!IsUsable()) return;
            if (resultStart == null || tier < 0 || tier >= resultStart.Length) return;

            _tier = tier;
            player.SetTime(resultStart[tier]);
            player.Play();
            _state = StateResult;
        }

        /// <summary>演出を終えて、画面の背景に戻す</summary>
        public void Standby()
        {
            if (screenObject != null) screenObject.SetActive(false);
            PlayBackground();
        }

        /// <summary>画面の背景として流す。普段はこの状態</summary>
        public void PlayBackground()
        {
            if (!IsUsable() && _state != StateLoading) return;
            if (player == null) return;

            _state = StateBackground;
            player.SetTime(backgroundStart);

            if (backgroundScreen != null) backgroundScreen.SetActive(true);
            ApplyBackgroundPlayback();
        }

        /// <summary>近くにいるかどうかで、背景を流すか止めるかを決める</summary>
        private void ApplyBackgroundPlayback()
        {
            if (player == null) return;

            if (_nearby)
            {
                player.Play();
                _bgPaused = false;
            }
            else
            {
                player.Pause();
                _bgPaused = true;
            }
        }

        private void Update()
        {
            if (_state != StateIntro && _state != StateLoop &&
                _state != StateResult && _state != StateBackground) return;
            if (player == null) return;

            if (_state == StateBackground)
            {
                if (_bgPaused) return;
                if (player.GetTime() >= backgroundEnd) player.SetTime(backgroundStart);
                return;
            }

            var t = player.GetTime();

            if (_state == StateIntro)
            {
                // 導入がループのはじまりまで流れたら、ループ扱いに切り替える。
                // ここは飛ばさずそのまま流すので継ぎ目は出ない
                if (t >= loopStart) _state = StateLoop;
                return;
            }

            if (_state == StateLoop)
            {
                if (t >= loopEnd) player.SetTime(loopStart);
                return;
            }

            // 結果の保持ループ。次の区間へなだれ込ませない
            if (holdEnd == null || _tier < 0 || _tier >= holdEnd.Length) return;
            if (t >= holdEnd[_tier]) player.SetTime(holdStart[_tier]);
        }

        /// <summary>案内を閉じる。OKボタンから呼ぶ</summary>
        public void HideNotice()
        {
            if (blockedNotice != null) blockedNotice.SetActive(false);
        }

        // ============================================================
        //  外から見る用
        // ============================================================
        public bool IsUsable()
        {
            if (player == null) return false;
            return _state != StateLoading && _state != StateFailed && _state != StateBlocked;
        }

        public bool IsReady() { return _state == StateStandby; }
        public bool IsBlocked() { return _state == StateBlocked; }
        public bool IsFailed() { return _state == StateFailed; }
        public bool IsLoading() { return _state == StateLoading; }
        public int GetState() { return _state; }

        /// <summary>導入が終わってループに入ったか。TAPを出してよいかの目安</summary>
        public bool IsLooping() { return _state == StateLoop; }

        private void Log(string message)
        {
            if (debugPanel != null) debugPanel.Log(message);
            Debug.Log("[MyDarts] " + message);
        }
    }
}
