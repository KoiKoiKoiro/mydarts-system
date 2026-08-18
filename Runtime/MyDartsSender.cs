using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.SDK3.Data;
using VRC.SDK3.StringLoading;
using VRC.SDK3.Persistence;

namespace MyDartsSystem
{
    /// <summary>
    /// 台帳への書き込み。
    ///
    /// UdonはURLを実行時に組み立てられないので、
    /// 「あらかじめ焼き込んでおいたURLのうち、どれを叩いたか」で値を伝える。
    ///
    /// 送り方は2通りある。
    ///
    /// ■ 高速（1本・約3秒）
    ///     w = 種別 × 200 + 値
    ///   サーバーが「この接続元は何番の人か」を覚えている場合に使える。
    ///   覚えているかどうかは Cloudflare Worker が付ける識別子で決まる。
    ///
    /// ■ 従来（3本・約17秒）
    ///     ① a = 番号の上2桁 × 10 + 種別
    ///     ② b = 番号の下2桁
    ///     ③ v = 値
    ///   サーバーが覚えていないとき、または同じ回線に複数人いて
    ///   区別が付かないときは、こちらへ自動で切り替わる。
    ///
    /// 入室してしばらくすると、裏で「名乗るだけ」の送信（種別0）を1回行う。
    /// これが通れば以降の送信はすべて高速側になる。台帳は変化しない。
    ///
    /// VRChatのダウンロードは5秒に1回までなので、複数本のときは間隔を空ける。
    ///
    /// ※ ワールド内に出す文字列だけは、フォントの都合で英語にしてある。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsSender : UdonSharpBehaviour
    {
        // 種別コード。GAS側と揃えること
        public const int KindBind = 0; // 台帳は変えない。名乗るだけ
        public const int KindTip = 1;
        public const int KindBarrel = 2;
        public const int KindShaft = 3;
        public const int KindFlight = 4;
        public const int KindFlightShape = 5;
        public const int KindCoinAdd1 = 6;
        public const int KindCoinAdd100 = 7;
        public const int KindCoinSub1 = 8;
        public const int KindCoinSub100 = 9;

        // ガチャ。3本方式では送れないので、名乗り済みのときだけ使える
        public const int KindGachaOne = 10;
        public const int KindGachaSet = 11;

        private const int ValueSpan = 200; // 値の取り得る範囲。GAS側と揃えること

        [Header("=== 焼き込みURL ===")]
        [SerializeField, Tooltip("添字 = 種別×200 + 値（0〜1799）。1本で送るとき用")]
        private VRCUrl[] wUrls;

        [SerializeField, Tooltip("添字 = 番号上2桁×10 + 種別（0〜299）")]
        private VRCUrl[] aUrls;

        [SerializeField, Tooltip("添字 = 番号下2桁（0〜99）")]
        private VRCUrl[] bUrls;

        [SerializeField, Tooltip("添字 = 値（0〜199）")]
        private VRCUrl[] vUrls;

        [Header("=== 参照 ===")]
        [SerializeField, Tooltip("自分の番号を持っている読み込み側")]
        private MyDartsFetcher fetcher;

        [SerializeField, Tooltip("プレイヤー向けの状況パネル")]
        private MyDartsStatusPanel statusPanel;

        [SerializeField, Tooltip("開発用のログパネル")]
        private MyDartsDebugPanel debugPanel;

        [SerializeField, Tooltip("送信中に押せなくするボタン。ショップのボタンもここに入れる")]
        private Button[] lockButtons;

        [Header("=== 動作 ===")]
        [SerializeField, Range(5f, 15f), Tooltip("1本ごとの間隔。VRChatの制限は5秒なので余裕を持たせる")]
        private float stepInterval = 6f;

        [SerializeField, Range(0, 5), Tooltip("BUSYで弾かれたときにやり直す回数")]
        private int maxRetry = 2;

        [Header("=== クールダウン（サーバー保護・連打防止） ===")]
        [SerializeField, Range(0f, 300f), Tooltip("コイン増減の間隔。連打防止用。負荷対策は獲得をまとめる側で行う")]
        private float coinCooldown = 5f;

        [SerializeField, Range(0f, 60f), Tooltip("パーツ購入の間隔")]
        private float buyCooldown = 5f;

        [SerializeField, Range(1f, 15f), Tooltip("送信後にボタンを閉じておく秒数。VRChatの5秒制限に合わせる")]
        private float lockSeconds = 5f;

        [Header("=== 名乗り（高速化） ===")]
        [SerializeField, Tooltip("入室後に自動で名乗る。切ると常に3本方式になる")]
        private bool bindOnStart = true;

        [SerializeField, Range(5f, 60f), Tooltip("入室から名乗るまでの待ち秒数。台帳の読み込みと重ならないよう遅らせる")]
        private float bindDelay = 10f;

        [Header("=== テスト用 ===")]
        [SerializeField, Range(1, 199), Tooltip("テストボタンで増減させる額。100倍されるので 10 なら 1000 コイン")]
        private int testAmountHundreds = 10;

        // 進行状態
        private const int StateIdle = 0;
        private const int StateA = 1;
        private const int StateB = 2;
        private const int StateV = 3;
        private const int StateBindA = 4;
        private const int StateBindB = 5;
        private const int StateW = 6;

        private int _state;
        private int _kind;
        private int _value;
        private int _retry;
        private int _coinAfter = -1;

        // サーバーが自分を覚えているか
        private bool _bound;
        private int _bindTries;

        // ボタンを押せなくしているか。
        // 送信が終わってもVRChatの5秒制限が残るので、少し余分に閉じておく。
        private bool _locked;

        // 用途ごとのクールダウン。次に送ってよくなる時刻を持つ
        private float _coinReadyAt;
        private float _buyReadyAt;

        // まとめ送信のカート。同じ種別は上書きするので無駄打ちが出ない
        private const int QueueMax = 8;
        private int[] _qKind;
        private int[] _qValue;
        private int _qCount;
        private int _qIndex;

        // ローカルで持っている残高。
        // コイン増減は結果を待たずに返ってくるので、こちらで加減算する。
        private int _coin = -1;

        // 実測用
        private float _stepStartedAt;
        private float _totalStartedAt;
        private float _netTotal;

        private const string KeyCoin = "mydarts_coin";

        public bool IsBusy() { return _state != StateIdle; }
        public bool IsBound() { return _bound; }
        public int GetLastCoin() { return _coinAfter; }

        private void Start()
        {
            if (bindOnStart)
            {
                SendCustomEventDelayedSeconds(nameof(TryBind), bindDelay);
            }
        }

        // ============================================================
        //  ボタンから呼ぶ入口
        // ============================================================
        public void OnClickAddCoin()
        {
            Begin(KindCoinAdd100, testAmountHundreds);
        }

        public void OnClickSubCoin()
        {
            Begin(KindCoinSub100, testAmountHundreds);
        }

        /// <summary>ガチャ（単発）。名乗り済みでないと使えない</summary>
        public void OnClickGachaOne()
        {
            BeginGacha(KindGachaOne);
        }

        /// <summary>ガチャ（4種セット）</summary>
        public void OnClickGachaSet()
        {
            BeginGacha(KindGachaSet);
        }

        private void BeginGacha(int kind)
        {
            if (!_bound)
            {
                Report("Preparing... try again in a moment.");
                TryBind();
                return;
            }
            Begin(kind, 0);
        }

        /// <summary>
        /// 保存を開始する。他のスクリプトからも呼べる。
        /// value は 0〜199。それ以上の金額は ×100 の種別を使う。
        /// </summary>
        public void Begin(int kind, int value)
        {
            ClearQueue();
            if (!Enqueue(kind, value)) return;
            FlushQueue();
        }

        // ============================================================
        //  カート
        //
        //  1つずつ送ると、そのたびに5秒の待ちが挟まって遅い。
        //  先にまとめてから一度に流すと、待ちは送信と送信の間だけになり、
        //  プレイヤーから見れば「1回のロード」で済む。
        //
        //  同じ種別を入れ直したときは上書きする。
        //  バレルを何度も選び直しても、送るのは最後の1つだけ。
        // ============================================================

        public int GetQueueCount() { return _qCount; }

        /// <summary>まだ台帳へ送り終えていないものがあるか</summary>
        public bool IsSaving() { return _pumping || _qCount > 0; }

        // 送信ループが回っているか
        private bool _pumping;

        // 待ち直しの予約が入っているか（予約の重複を防ぐ）
        private bool _pumpWaiting;

        /// <summary>その種別がカートに入っているか</summary>
        public bool IsInQueue(int kind)
        {
            for (var i = 0; i < _qCount; i++)
            {
                if (_qKind[i] == kind) return true;
            }
            return false;
        }

        /// <summary>カートに入っているその種別の値</summary>
        public int QueuedValueOf(int kind)
        {
            for (var i = 0; i < _qCount; i++)
            {
                if (_qKind[i] == kind) return _qValue[i];
            }
            return -1;
        }

        /// <summary>カートに積む。まだ送らない</summary>
        public bool Enqueue(int kind, int value)
        {
            if (kind < 1 || kind > 11)
            {
                Fail("Invalid kind: " + kind);
                return false;
            }

            if (value < 0 || value >= ValueSpan)
            {
                Fail("Value out of range: " + value);
                return false;
            }

            if (_qKind == null || _qKind.Length < QueueMax)
            {
                _qKind = new int[QueueMax];
                _qValue = new int[QueueMax];
                _qCount = 0;
            }

            // 同じ種別があれば差し替える
            for (var i = 0; i < _qCount; i++)
            {
                if (_qKind[i] == kind)
                {
                    _qValue[i] = value;
                    return true;
                }
            }

            if (_qCount >= QueueMax)
            {
                Report("Cart is full.");
                return false;
            }

            _qKind[_qCount] = kind;
            _qValue[_qCount] = value;
            _qCount++;
            return true;
        }

        public void ClearQueue()
        {
            _qCount = 0;
            _qIndex = 0;
        }

        // ============================================================
        //  装備の付け替え（所持済み＝無料のもの）
        //
        //  VRChatはダウンロードを5秒に1回しか許さないので、
        //  4パーツ送ると台帳に届くまで20秒近くかかる。
        //  そこで、
        //    ・手元の装備とパーシスタンスは押した瞬間に書き換える
        //    ・台帳への送信は裏でループさせる
        //    ・送信の途中で選び直したら、最後に決めたものだけを送る
        //  という形にしてある。プレイヤーは待たされない。
        // ============================================================

        /// <summary>今すぐ装備して、台帳への保存は裏で進める</summary>
        public void EquipNow(int kind, int value)
        {
            if (kind < KindTip || kind > KindFlightShape) return;
            if (fetcher == null) return;

            // 所持済み（と形状）は代金がかからず必ず通るので、先に反映してよい
            var free = (kind == KindFlightShape) || fetcher.IsOwned(kind, value);
            if (free) fetcher.SetEquipped(kind, value);

            if (!Enqueue(kind, value)) return;

            PumpQueue();
        }

        /// <summary>
        /// 送信ループを回す。
        /// もう回っていれば何もしない。まだ送れない状態なら少し待って出直す。
        /// </summary>
        public void PumpQueue()
        {
            _pumpWaiting = false;

            if (_pumping) return;
            if (_qCount <= 0) return;

            if (_state != StateIdle || Time.realtimeSinceStartup < _buyReadyAt)
            {
                SchedulePump();
                return;
            }

            StartPump();
        }

        private void SchedulePump()
        {
            if (_pumpWaiting) return;
            _pumpWaiting = true;
            SendCustomEventDelayedSeconds(nameof(PumpQueue), 1f);
        }

        private void StartPump()
        {
            _pumping = true;
            _netTotal = 0f;
            _totalStartedAt = Time.realtimeSinceStartup;
            SendQueued();
        }

        /// <summary>カートの中身を順に送る</summary>
        public void FlushQueue()
        {
            if (_qCount <= 0)
            {
                Report("Nothing to save.");
                return;
            }

            // すでにループが回っているなら、積んだ分は勝手に拾われる
            if (_pumping)
            {
                Report("Saving in the background.");
                return;
            }

            if (_locked || _state != StateIdle)
            {
                Report("Please wait a moment.");
                return;
            }

            // 中身に応じてクールダウンを見る
            var now = Time.realtimeSinceStartup;
            var hasCoin = false;
            var hasPart = false;

            for (var i = 0; i < _qCount; i++)
            {
                if (IsCoinKind(_qKind[i])) hasCoin = true;
                else hasPart = true;
            }

            if (hasCoin && now < _coinReadyAt)
            {
                Report("Please wait " + Mathf.CeilToInt(_coinReadyAt - now) + "s.");
                return;
            }
            if (hasPart && now < _buyReadyAt)
            {
                Report("Please wait " + Mathf.CeilToInt(_buyReadyAt - now) + "s.");
                return;
            }

            // 所持済みのものは見た目だけ先に変えてしまう
            PreApplyOwnedParts();

            Lock();
            StartPump();
        }

        /// <summary>所持済みの付け替えだけ、送信前に手元へ反映する</summary>
        private void PreApplyOwnedParts()
        {
            if (fetcher == null) return;

            for (var i = 0; i < _qCount; i++)
            {
                var kind = _qKind[i];
                var value = _qValue[i];

                // パーツ以外（コイン・ガチャ）はサーバーの答えを待つ
                if (kind < KindTip || kind > KindFlightShape) continue;

                // 買うものは代金の判定が要るので待つ。形状は常に無料
                if (kind != KindFlightShape && !fetcher.IsOwned(kind, value)) continue;

                fetcher.SetEquipped(kind, value);
            }
        }

        /// <summary>カートの先頭を送る</summary>
        public void SendQueued()
        {
            if (_qCount <= 0)
            {
                FinishQueue();
                return;
            }

            // 常に先頭から送る。送り終えたものだけ取り除いていく
            _kind = _qKind[0];
            _value = _qValue[0];
            _retry = 0;
            _coinAfter = -1;

            if (_qCount > 1)
            {
                Progress("Saving...", _qCount + " left");
            }

            if (_bound && HasFastUrl(_kind, _value))
            {
                StartFast();
            }
            else
            {
                StartSequence();
            }
        }

        // ============================================================
        //  ガチャの結果
        // ============================================================
        private int _lastDesign = -1;
        private int _lastTier;
        private bool _lastWasSet;
        private bool _lastWasDup;

        // 単発で当たったパーツ。1=チップ 2=バレル 3=シャフト 4=フライト
        // セットのときは 0（4種すべて）
        private int _lastPart;

        public int GetLastDesign() { return _lastDesign; }
        public int GetLastTier() { return _lastTier; }
        public bool GetLastWasSet() { return _lastWasSet; }
        public bool GetLastWasDup() { return _lastWasDup; }
        public int GetLastPart() { return _lastPart; }

        /// <summary>「BARREL 47」「FULL SET 87」のような1行を作る</summary>
        public string GetLastResultLine()
        {
            if (_lastDesign < 0) return "-";
            if (_lastWasSet) return "FULL SET   " + _lastDesign;
            return PartName(_lastPart) + "   " + _lastDesign;
        }

        public string PartName(int part)
        {
            if (part == 1) return "TIP";
            if (part == 2) return "BARREL";
            if (part == 3) return "SHAFT";
            if (part == 4) return "FLIGHT";
            return "SET";
        }

        private void ApplyGachaResult(DataDictionary dict)
        {
            _lastDesign = ReadInt(dict, "design", -1);
            _lastTier = ReadInt(dict, "tier", 0);
            _lastWasSet = ReadInt(dict, "set", 0) == 1;
            _lastWasDup = ReadInt(dict, "dup", 0) == 1;

            if (_lastDesign < 0) return;

            var tip = ReadInt(dict, "tip", -1);
            var barrel = ReadInt(dict, "barrel", -1);
            var shaft = ReadInt(dict, "shaft", -1);
            var flight = ReadInt(dict, "flight", -1);

            // どのパーツに乗ったか。サーバーが part を返すならそれが正。
            // 返さない場合は、変わった箇所から見当をつける。
            _lastPart = ReadInt(dict, "part", 0);

            if (!_lastWasSet && _lastPart <= 0 && fetcher != null)
            {
                _lastPart = GuessPart(tip, barrel, shaft, flight);
            }

            if (fetcher != null)
            {
                ApplyPart(1, tip);
                ApplyPart(2, barrel);
                ApplyPart(3, shaft);
                ApplyPart(4, flight);
            }

            if (statusPanel != null)
            {
                statusPanel.ShowSuccess(
                    GetLastResultLine(),
                    TierName(_lastTier) + (_lastWasDup ? "" : "      NEW!") +
                    "\nCoins: " + GetCoin());
            }
            if (debugPanel != null)
            {
                debugPanel.Log("gacha: " + GetLastResultLine() +
                               " " + TierName(_lastTier) +
                               (_lastWasDup ? " dup" : " new"));
            }
        }

        /// <summary>
        /// 単発でどのパーツが変わったかを、いまの装備との差から当てる。
        /// 同じ柄がもう一度出た場合は差が出ないので、そのときは 0 のままにする。
        /// </summary>
        private int GuessPart(int tip, int barrel, int shaft, int flight)
        {
            if (tip >= 0 && tip != fetcher.GetTip()) return 1;
            if (barrel >= 0 && barrel != fetcher.GetBarrel()) return 2;
            if (shaft >= 0 && shaft != fetcher.GetShaft()) return 3;
            if (flight >= 0 && flight != fetcher.GetFlight()) return 4;
            return 0;
        }

        private void ApplyPart(int kind, int value)
        {
            if (value < 0) return;
            fetcher.MarkOwned(kind, value);
            fetcher.SetEquipped(kind, value);
        }

        private string TierName(int tier)
        {
            if (tier <= 0) return "COMMON";
            if (tier == 1) return "RARE";
            if (tier == 2) return "VERY RARE";
            return "ULTRA RARE";
        }

        private void AdvanceQueue()
        {
            StartCooldown(_kind);

            // 送り終えたものを外す。
            // 送っている最中に選び直されていたら値が変わっているので、
            // その場合は外さずに残し、新しい値でもう一度送る。
            RemoveIfUnchanged(_kind, _value);

            if (_qCount > 0)
            {
                // 次の1件へ。VRChatの5秒制限があるので間隔を空ける
                SendCustomEventDelayedSeconds(nameof(SendQueued), stepInterval);
                return;
            }

            FinishQueue();
        }

        /// <summary>送った内容が今も同じなら、カートから外す</summary>
        private void RemoveIfUnchanged(int kind, int value)
        {
            for (var i = 0; i < _qCount; i++)
            {
                if (_qKind[i] != kind) continue;

                // 送信中に選び直された → 残して送り直す
                if (_qValue[i] != value) return;

                for (var j = i; j < _qCount - 1; j++)
                {
                    _qKind[j] = _qKind[j + 1];
                    _qValue[j] = _qValue[j + 1];
                }
                _qCount--;
                return;
            }
        }

        private void FinishQueue()
        {
            _pumping = false;
            ClearQueue();
            ScheduleUnlock();

            if (statusPanel != null)
            {
                statusPanel.ShowSuccess("Saved", "Coins: " + GetCoin());
            }
            if (debugPanel != null)
            {
                var wall = Time.realtimeSinceStartup - _totalStartedAt;
                debugPanel.SetState("Idle");
                debugPanel.Log("queue done. TOTAL " + Ms(wall) + "ms / NET " + Ms(_netTotal) + "ms");
            }
        }

        // ============================================================
        //  名乗る（種別0）
        //  台帳は変わらない。サーバーに自分の番号を覚えさせるだけ。
        // ============================================================
        /// <summary>
        /// 名乗り直す。登録が通った直後に呼ぶ。
        ///
        /// 未登録のうちは番号が取れないので数回であきらめる作りになっている。
        /// 登録できたら数え直して、もう一度名乗る。
        /// </summary>
        public void RetryBind()
        {
            _bound = false;
            _bindTries = 0;
            TryBind();
        }

        public void TryBind()
        {
            if (_bound) return;

            if (_state != StateIdle)
            {
                SendCustomEventDelayedSeconds(nameof(TryBind), stepInterval);
                return;
            }

            var number = ReadMyNumber();
            if (number <= 0)
            {
                // まだ台帳の読み込みが終わっていない。数回だけ待ってみる
                _bindTries++;
                if (_bindTries <= 6)
                {
                    SendCustomEventDelayedSeconds(nameof(TryBind), bindDelay);
                }
                else if (debugPanel != null)
                {
                    debugPanel.Log("bind: gave up (not registered)");
                }
                return;
            }

            var hi = (number - 1) / 100;
            var index = hi * 10 + KindBind;

            if (aUrls == null || index < 0 || index >= aUrls.Length || aUrls[index] == null)
            {
                if (debugPanel != null) debugPanel.Log("bind: url missing (a=" + index + ")");
                return;
            }

            _state = StateBindA;
            Lock();
            if (debugPanel != null) debugPanel.Log("bind a=" + index);

            _stepStartedAt = Time.realtimeSinceStartup;
            VRCStringDownloader.LoadUrl(aUrls[index], this);
        }

        public void BindStepB()
        {
            var number = ReadMyNumber();
            var lo = (number - 1) % 100;

            if (bUrls == null || lo < 0 || lo >= bUrls.Length || bUrls[lo] == null)
            {
                _state = StateIdle;
                ScheduleUnlock();
                return;
            }

            _state = StateBindB;
            if (debugPanel != null) debugPanel.Log("bind b=" + lo);

            _stepStartedAt = Time.realtimeSinceStartup;
            VRCStringDownloader.LoadUrl(bUrls[lo], this);
        }

        // ============================================================
        //  高速送信（1本）
        // ============================================================
        private bool HasFastUrl(int kind, int value)
        {
            if (wUrls == null) return false;
            var index = kind * ValueSpan + value;
            return index >= 0 && index < wUrls.Length && wUrls[index] != null;
        }

        public void StartFast()
        {
            if (!HasFastUrl(_kind, _value))
            {
                // 焼き込み漏れなら従来方式へ
                StartSequence();
                return;
            }

            var index = _kind * ValueSpan + _value;

            _state = StateW;
            Progress("Saving...", "Sending");
            if (debugPanel != null) debugPanel.Log("send w=" + index + " (fast)");

            _stepStartedAt = Time.realtimeSinceStartup;
            VRCStringDownloader.LoadUrl(wUrls[index], this);
        }

        // ============================================================
        //  従来の3本方式
        // ============================================================
        public void StartSequence()
        {
            var number = ReadMyNumber();
            if (number <= 0)
            {
                Fail("You are not registered yet.");
                return;
            }

            var hi = (number - 1) / 100;
            var index = hi * 10 + _kind;

            if (aUrls == null || index < 0 || index >= aUrls.Length || aUrls[index] == null)
            {
                Fail("URL is missing (a=" + index + "). Rebuild the URL list in the editor.");
                return;
            }

            _state = StateA;
            Progress("Saving...", "Step 1 of 3");
            if (debugPanel != null) debugPanel.Log("send a=" + index + " (kind=" + _kind + ")");

            _stepStartedAt = Time.realtimeSinceStartup;
            VRCStringDownloader.LoadUrl(aUrls[index], this);
        }

        public void StepB()
        {
            var number = ReadMyNumber();
            var lo = (number - 1) % 100;

            if (bUrls == null || lo < 0 || lo >= bUrls.Length || bUrls[lo] == null)
            {
                Fail("URL is missing (b=" + lo + "). Rebuild the URL list in the editor.");
                return;
            }

            _state = StateB;
            Progress("Saving...", "Step 2 of 3");
            if (debugPanel != null) debugPanel.Log("send b=" + lo);

            _stepStartedAt = Time.realtimeSinceStartup;
            VRCStringDownloader.LoadUrl(bUrls[lo], this);
        }

        public void StepV()
        {
            if (vUrls == null || _value < 0 || _value >= vUrls.Length || vUrls[_value] == null)
            {
                Fail("URL is missing (v=" + _value + "). Rebuild the URL list in the editor.");
                return;
            }

            _state = StateV;
            Progress("Saving...", "Step 3 of 3");
            if (debugPanel != null) debugPanel.Log("send v=" + _value);

            _stepStartedAt = Time.realtimeSinceStartup;
            VRCStringDownloader.LoadUrl(vUrls[_value], this);
        }

        // ============================================================
        //  レスポンス
        // ============================================================
        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            var body = result.Result;

            var rtt = Time.realtimeSinceStartup - _stepStartedAt;
            _netTotal += rtt;
            if (debugPanel != null)
            {
                debugPanel.Log("rtt " + Ms(rtt) + "ms  resp: " + Head(body, 90));
            }

            DataToken root;
            if (!VRCJson.TryDeserializeFromJson(body, out root) ||
                root.TokenType != TokenType.DataDictionary)
            {
                Fail("Could not read the server response.");
                return;
            }

            var dict = root.DataDictionary;

            DataToken okToken;
            var ok = dict.TryGetValue("ok", out okToken)
                     && okToken.TokenType == TokenType.Boolean
                     && okToken.Boolean;

            if (!ok)
            {
                HandleError(ReadString(dict, "err", "UNKNOWN"), dict);
                return;
            }

            // ---------- 名乗りの途中 ----------
            if (_state == StateBindA)
            {
                SendCustomEventDelayedSeconds(nameof(BindStepB), stepInterval);
                return;
            }

            if (_state == StateBindB)
            {
                _state = StateIdle;
                ScheduleUnlock();

                DataToken boundToken;
                _bound = dict.TryGetValue("bound", out boundToken)
                         && boundToken.TokenType == TokenType.Boolean
                         && boundToken.Boolean;

                if (debugPanel != null)
                {
                    debugPanel.Log(_bound
                        ? "bind ok -> fast mode"
                        : "bind failed (" + ReadString(dict, "reason", "?") + ") -> 3-step mode");
                }
                return;
            }

            // ---------- 送信の途中 ----------
            if (_state == StateA)
            {
                SendCustomEventDelayedSeconds(nameof(StepB), stepInterval);
                return;
            }

            if (_state == StateB)
            {
                SendCustomEventDelayedSeconds(nameof(StepV), stepInterval);
                return;
            }

            // ---------- 完了 ----------
            // 3本方式が通ったならサーバー側も覚えているので、次から高速になる
            if (_state == StateV) _bound = true;

            _state = StateIdle;

            // サーバーが残高を返してきたらそれが正。
            // 返ってこない（先に受付だけ返された）ときは自分で加減算する。
            var reported = ReadInt(dict, "coin", -1);
            if (reported >= 0)
            {
                SetCoin(reported);
            }
            else if (IsCoinKind(_kind))
            {
                SetCoin(GetCoin() + CoinDelta(_kind, _value));
            }
            _coinAfter = GetCoin();

            // ガチャは「何が出たか」がレスポンスに入っている
            if (IsGachaKind(_kind))
            {
                ApplyGachaResult(dict);
            }
            else if (!IsCoinKind(_kind) && fetcher != null)
            {
                // パーツを買ったら、ワールド側の所持リストと装備も更新する。
                // これをしないと、買った直後なのに「所持していない」表示のままになる。
                fetcher.MarkOwned(_kind, _value);
                fetcher.SetEquipped(_kind, _value);
            }

            if (debugPanel != null)
            {
                debugPanel.Log("saved. kind=" + _kind + " value=" + _value + " coin=" + _coinAfter);
            }

            AdvanceQueue();
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            var err = (result != null && !string.IsNullOrEmpty(result.Error)) ? result.Error : "unknown";
            if (debugPanel != null) debugPanel.Log("send error: " + err);

            if (_state == StateBindA || _state == StateBindB)
            {
                _state = StateIdle;
                ScheduleUnlock();
                return;
            }

            RetryOrGiveUp();
        }

        // ============================================================
        //  エラーの振り分け
        // ============================================================
        private void HandleError(string err, DataDictionary dict)
        {
            // 名乗りの失敗は黙って諦める。3本方式で動けるので実害はない
            if (_state == StateBindA || _state == StateBindB)
            {
                _state = StateIdle;
                ScheduleUnlock();
                if (debugPanel != null) debugPanel.Log("bind error: " + err);
                return;
            }

            // サーバーが自分を覚えていない／同じ回線に複数人いて区別できない
            if (err == "NOT_BOUND" || err == "AMBIGUOUS" || err == "NO_SID")
            {
                _bound = false;
                _state = StateIdle;

                if (debugPanel != null) debugPanel.Log("fast rejected (" + err + ") -> falling back");

                // クールダウンを待ってから3本方式でやり直す
                SendCustomEventDelayedSeconds(nameof(StartSequence), stepInterval);
                return;
            }

            if (err == "BUSY")
            {
                RetryOrGiveUp();
                return;
            }

            if (err == "NOT_ENOUGH")
            {
                _state = StateIdle;
                ScheduleUnlock();
                var have = ReadInt(dict, "coin", 0);
                var price = ReadInt(dict, "price", 0);
                if (statusPanel != null)
                {
                    statusPanel.ShowError("Not enough coins",
                        "You have " + have + ", this costs " + price + ".");
                }
                if (debugPanel != null)
                {
                    debugPanel.Log("not enough: have=" + have + " price=" + price);
                }
                return;
            }

            if (err == "DAILY_CAP")
            {
                _state = StateIdle;
                ScheduleUnlock();
                if (statusPanel != null)
                {
                    statusPanel.ShowError("Daily limit reached",
                        "You have earned the maximum for today.");
                }
                if (debugPanel != null) debugPanel.Log("daily cap reached");
                return;
            }

            Fail("Server error: " + err);
        }

        // ============================================================
        //  やり直し
        // ============================================================
        private void RetryOrGiveUp()
        {
            if (_retry >= maxRetry)
            {
                Fail("The server was busy. Please try again in a moment.");
                return;
            }

            _retry++;
            _state = StateIdle;

            Progress("Retrying...", "Attempt " + (_retry + 1));
            if (debugPanel != null) debugPanel.Log("retry " + _retry + "/" + maxRetry);

            SendCustomEventDelayedSeconds(nameof(StartSequence), stepInterval + _retry * 3f);
        }

        // ============================================================
        //  小物
        // ============================================================
        /// <summary>いまの残高。台帳を読んだ値か、前回保存した値</summary>
        public int GetCoin()
        {
            if (_coin >= 0) return _coin;

            var local = Networking.LocalPlayer;
            if (Utilities.IsValid(local) && PlayerData.HasKey(local, KeyCoin))
            {
                _coin = PlayerData.GetInt(local, KeyCoin);
                return _coin;
            }

            // 台帳がまだ読めていないうちは覚え込まない。
            // ここで 0 を握ってしまうと、あとで登録が通っても 0 のままになる。
            if (fetcher == null || !fetcher.IsFound()) return 0;

            _coin = fetcher.GetMyCoin();
            return _coin < 0 ? 0 : _coin;
        }

        /// <summary>登録が通った直後など、外から残高を教える</summary>
        public void AdoptCoin(int value)
        {
            SetCoin(value);
        }

        private void SetCoin(int value)
        {
            if (value < 0) value = 0;
            _coin = value;

            var local = Networking.LocalPlayer;
            if (Utilities.IsValid(local)) PlayerData.SetInt(KeyCoin, value);
        }

        /// <summary>種別と値から、残高がいくら動くかを求める</summary>
        private int CoinDelta(int kind, int value)
        {
            if (kind == KindCoinAdd1) return value;
            if (kind == KindCoinAdd100) return value * 100;
            if (kind == KindCoinSub1) return -value;
            if (kind == KindCoinSub100) return -value * 100;
            return 0;
        }

        private bool IsCoinKind(int kind)
        {
            return kind >= KindCoinAdd1 && kind <= KindCoinSub100;
        }

        private bool IsGachaKind(int kind)
        {
            return kind == KindGachaOne || kind == KindGachaSet;
        }

        private int ReadMyNumber()
        {
            if (fetcher == null) return 0;
            var s = fetcher.GetMyNumber();
            if (string.IsNullOrEmpty(s)) return 0;

            var n = 0;
            var any = false;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c < '0' || c > '9') continue;
                n = n * 10 + (c - '0');
                any = true;
            }
            return any ? n : 0;
        }

        private void Progress(string title, string body)
        {
            if (statusPanel != null) statusPanel.ShowInfo(title, body);
            if (debugPanel != null) debugPanel.SetState(title + " " + body);
        }

        private void Report(string message)
        {
            if (statusPanel != null) statusPanel.ShowInfo("Please Wait", message);
            if (debugPanel != null) debugPanel.Log(message);
        }

        /// <summary>送信が通ったので、その用途のクールダウンを開始する</summary>
        private void StartCooldown(int kind)
        {
            var now = Time.realtimeSinceStartup;
            if (IsCoinKind(kind)) _coinReadyAt = now + coinCooldown;
            else _buyReadyAt = now + buyCooldown;
        }

        /// <summary>ボタンを押せなくする</summary>
        private void Lock()
        {
            _locked = true;
            ApplyLock();
        }

        /// <summary>
        /// 送信が終わってから少し置いて解除する。
        /// VRChatは5秒に1回しかダウンロードできないので、
        /// 直後に押されると次の送信が待たされてしまう。
        /// </summary>
        private void ScheduleUnlock()
        {
            SendCustomEventDelayedSeconds(nameof(Unlock), lockSeconds);
        }

        public void Unlock()
        {
            _locked = false;
            ApplyLock();
        }

        private void ApplyLock()
        {
            if (lockButtons == null) return;
            for (var i = 0; i < lockButtons.Length; i++)
            {
                if (lockButtons[i] != null) lockButtons[i].interactable = !_locked;
            }
        }

        private void Fail(string message)
        {
            _state = StateIdle;
            ScheduleUnlock();
            if (statusPanel != null) statusPanel.ShowError("Save Failed", message);
            if (debugPanel != null)
            {
                debugPanel.SetState("Idle");
                debugPanel.Log(message);
            }
        }

        /// <summary>秒をミリ秒の整数にして返す</summary>
        private int Ms(float seconds)
        {
            return (int)(seconds * 1000f);
        }

        private string Head(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private string ReadString(DataDictionary dict, string key, string fallback)
        {
            DataToken t;
            if (dict.TryGetValue(key, out t) && t.TokenType == TokenType.String) return t.String;
            return fallback;
        }

        private int ReadInt(DataDictionary dict, string key, int fallback)
        {
            DataToken t;
            if (!dict.TryGetValue(key, out t)) return fallback;
            if (t.TokenType == TokenType.Double) return (int)t.Double;
            if (t.TokenType == TokenType.Float) return (int)t.Float;
            if (t.TokenType == TokenType.Int) return t.Int;
            return fallback;
        }
    }
}
