using UdonSharp;
using UnityEngine;
using TMPro;

namespace MyDartsSystem
{
    /// <summary>
    /// ガチャ演出の進行役。押してから結果を見せ終わるまでの流れを回す。
    ///
    ///   押す      … その瞬間にサーバーへ送る（当たりはここで決まっている）
    ///   導入      … 動画を流す。だいたい5秒
    ///   待機ループ … 返事が来るまで、来ていても導入が終わるまで回す
    ///   TAP!      … 両方そろったら光らせる
    ///   タップ    … レア度の区間へ飛ばす
    ///   登場      … 少し遅れて、当たったパーツが拡大縮小＋回転で出てくる
    ///   閉じる    … 真っ黒に戻して、元の画面へ
    ///
    /// 動画が使えない人（設定で止めている・読み込み失敗）でも
    /// 詰まらないように、動画抜きでも同じ流れが進むようにしてある。
    ///
    /// ※ ワールド内に出す文字列だけは、フォントの都合で英語にしてある。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsGachaStage : UdonSharpBehaviour
    {
        public const int StageIdle = 0;
        public const int StageIntro = 1;   // 導入・待機ループ
        public const int StageTap = 2;     // TAP待ち
        public const int StageReveal = 3;  // 結果を見せている

        [Header("=== 参照 ===")]
        [SerializeField] private MyDartsSender sender;
        [SerializeField] private MyDartsFetcher fetcher;
        [SerializeField] private MyDartsVideo video;
        [SerializeField] private MyDartsPreviewDart showcase;
        [SerializeField] private MyDartsScreens screens;
        [SerializeField] private MyDartsDebugPanel debugPanel;

        [Header("=== 見た目 ===")]
        [SerializeField, Tooltip("演出中だけ出す入れ物")]
        private GameObject stageGroup;

        [SerializeField, Tooltip("パーツを映すカメラ。使う時だけ入れる")]
        private Camera studioCamera;

        [SerializeField, Tooltip("TAP! の表示")]
        private GameObject tapHint;

        [SerializeField, Tooltip("結果の見出し。BARREL 47 など")]
        private TextMeshProUGUI resultLine;

        [SerializeField, Tooltip("レア度の文字")]
        private TextMeshProUGUI resultTier;

        [SerializeField, Tooltip("NEW! の表示")]
        private GameObject newBadge;

        [SerializeField, Tooltip("閉じるボタン。結果が出てから出す")]
        private GameObject closeButton;

        [Header("=== 間の取り方 ===")]
        [SerializeField, Range(1f, 10f), Tooltip("押してから TAP! を出すまでの最短時間（秒）")]
        private float minIntro = 5f;

        [SerializeField, Range(0f, 3f), Tooltip("飛ばしてからパーツが出てくるまで（秒）")]
        private float revealDelay = 0.55f;

        [SerializeField, Range(1f, 20f), Tooltip("返事が来ない時に諦めるまで（秒）")]
        private float giveUpAfter = 15f;

        private int _stage = StageIdle;
        private float _startedAt;
        private int _beforeDesign = -2;
        private bool _hasResult;
        private bool _tapShown;

        private void Start()
        {
            if (stageGroup != null) stageGroup.SetActive(false);
            if (tapHint != null) tapHint.SetActive(false);
            if (newBadge != null) newBadge.SetActive(false);
            if (closeButton != null) closeButton.SetActive(false);
            if (studioCamera != null) studioCamera.enabled = false;
        }

        // ============================================================
        //  ボタンから呼ぶ
        // ============================================================

        /// <summary>単発を引く</summary>
        public void OnPullOne()
        {
            if (!Begin()) return;
            if (sender != null) sender.OnClickGachaOne();
        }

        /// <summary>4種セットを引く</summary>
        public void OnPullSet()
        {
            if (!Begin()) return;
            if (sender != null) sender.OnClickGachaSet();
        }

        /// <summary>TAP! を押した</summary>
        public void OnTap()
        {
            if (_stage != StageTap) return;
            Reveal();
        }

        /// <summary>結果を閉じる</summary>
        public void OnClose()
        {
            _stage = StageIdle;

            if (video != null) video.Standby();
            if (showcase != null) showcase.ShowcaseHide();
            if (studioCamera != null) studioCamera.enabled = false;

            if (stageGroup != null) stageGroup.SetActive(false);
            if (tapHint != null) tapHint.SetActive(false);
            if (closeButton != null) closeButton.SetActive(false);
            if (newBadge != null) newBadge.SetActive(false);
        }

        // ============================================================
        //  流れ
        // ============================================================
        private bool Begin()
        {
            if (_stage != StageIdle) return false;

            // 直前の結果を覚えておく。これが変わったら「返事が来た」とみなす
            _beforeDesign = (sender != null) ? sender.GetLastDesign() : -2;
            _hasResult = false;
            _tapShown = false;
            _startedAt = Time.time;
            _stage = StageIntro;

            if (stageGroup != null) stageGroup.SetActive(true);
            if (tapHint != null) tapHint.SetActive(false);
            if (newBadge != null) newBadge.SetActive(false);
            if (closeButton != null) closeButton.SetActive(false);

            if (resultLine != null) resultLine.text = "";
            if (resultTier != null) resultTier.text = "";

            if (video != null) video.PlayIntro();

            Log("gacha: start");
            return true;
        }

        private void Update()
        {
            if (_stage != StageIntro) return;

            // 返事が来たか
            if (!_hasResult && sender != null && sender.GetLastDesign() != _beforeDesign)
            {
                _hasResult = true;
                Log("gacha: result arrived");
            }

            var waited = Time.time - _startedAt;

            // 諦めの時間。返事が来なくても止まったままにはしない
            if (!_hasResult && waited >= giveUpAfter)
            {
                Log("gacha: no answer");
                OnClose();
                return;
            }

            if (!_hasResult || waited < minIntro) return;

            // 導入が終わってループに入るまで待つ。
            // 動画が使えない人はこの条件を飛ばす
            if (video != null && video.IsUsable() && !video.IsLooping()) return;

            ShowTap();
        }

        private void ShowTap()
        {
            if (_tapShown) return;
            _tapShown = true;
            _stage = StageTap;

            if (tapHint != null) tapHint.SetActive(true);
            Log("gacha: tap ready");
        }

        private void Reveal()
        {
            _stage = StageReveal;

            if (tapHint != null) tapHint.SetActive(false);

            var tier = (sender != null) ? sender.GetLastTier() : 0;
            if (tier < 0) tier = 0;
            if (tier > 3) tier = 3;

            if (video != null) video.PlayResult(tier);

            // フラッシュが引くのに合わせてパーツを出す
            SendCustomEventDelayedSeconds(nameof(PopParts), revealDelay);
            Log("gacha: reveal tier " + tier);
        }

        /// <summary>パーツを出す。フラッシュの直後に呼ばれる</summary>
        public void PopParts()
        {
            if (_stage != StageReveal) return;
            if (sender == null) return;

            var design = sender.GetLastDesign();
            if (design < 0) return;

            if (studioCamera != null) studioCamera.enabled = true;

            if (showcase != null)
            {
                if (sender.GetLastWasSet()) showcase.ShowcaseSet(design);
                else showcase.ShowcasePart(sender.GetLastPart(), design);
            }

            if (resultLine != null) resultLine.text = sender.GetLastResultLine();

            if (resultTier != null)
            {
                var tier = sender.GetLastTier();
                resultTier.text = TierName(tier);
                resultTier.color = TierColor(tier);
            }

            if (newBadge != null) newBadge.SetActive(!sender.GetLastWasDup());
            if (closeButton != null) closeButton.SetActive(true);
        }

        // ============================================================
        //  見た目の細かいところ
        // ============================================================
        private string TierName(int tier)
        {
            if (tier <= 0) return "COMMON";
            if (tier == 1) return "RARE";
            if (tier == 2) return "VERY RARE";
            return "ULTRA RARE";
        }

        private Color TierColor(int tier)
        {
            if (tier <= 0) return new Color(0.78f, 0.83f, 0.87f, 1f);
            if (tier == 1) return new Color(0.21f, 0.78f, 1f, 1f);
            if (tier == 2) return new Color(0.30f, 0.49f, 1f, 1f);
            return new Color(1f, 1f, 1f, 1f);
        }

        public int GetStage() { return _stage; }
        public bool IsBusy() { return _stage != StageIdle; }

        private void Log(string message)
        {
            if (debugPanel != null) debugPanel.Log(message);
        }
    }
}
