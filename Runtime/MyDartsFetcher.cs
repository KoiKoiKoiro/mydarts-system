using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Data;
using VRC.SDK3.StringLoading;
using VRC.SDK3.Persistence;

namespace MyDartsSystem
{
    /// <summary>
    /// 台帳の読み込み。
    ///
    /// 入室したら台帳のJSONを1本取ってきて、
    /// 自分の displayName の行を探して復元する。
    /// 読み込みはURLが固定でよいので、送信と違って制約がない。
    ///
    /// 取得先は2種類を想定していて、どちらもこのスクリプトで扱える。
    ///   Cloudflare Worker … ?all=1 で全員分が返る（Untrusted設定が要る）
    ///   GitHub Pages      … 書き出し済みのJSONを置く（設定不要・公開向け）
    ///
    /// 番号はパーシスタンスにも控えておく。
    /// 取得に失敗しても、前回の値で動けるようにするため。
    ///
    /// ※ ワールド内に出す文字列だけは、フォントの都合で英語にしてある。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsFetcher : UdonSharpBehaviour
    {
        [Header("=== 取得先 ===")]
        [SerializeField, Tooltip("台帳JSONのURL。VRCUrlは実行時に作れないのでここで焼き込む")]
        private VRCUrl ledgerUrl;

        [Header("=== UI 参照 ===")]
        [SerializeField, Tooltip("プレイヤー向けの状況パネル")]
        private MyDartsStatusPanel statusPanel;

        [SerializeField, Tooltip("開発用のログパネル")]
        private MyDartsDebugPanel debugPanel;

        [SerializeField, Tooltip("登録UIをまとめた入れ物。登録済みなら隠す")]
        private GameObject registerGroup;

        [Header("=== 動作 ===")]
        [SerializeField, Tooltip("入室時に自動で取得する")]
        private bool fetchOnStart = true;

        [SerializeField, Range(1f, 30f), Tooltip("起動から取得までの待ち秒数")]
        private float startDelay = 3f;

        [SerializeField, Range(0, 5), Tooltip("読み込みに失敗したときにやり直す回数")]
        private int maxRetry = 3;

        [SerializeField, Range(6f, 30f), Tooltip("やり直すまでの待ち秒数。ダウンロードは5秒に1回までなので6秒以上")]
        private float retryDelay = 8f;

        // ---- 取得できた自分のデータ ----
        private bool _found;
        private string _myName = "";
        private string _myNumber = "";
        private int _myCoin;
        private int _tip, _barrel, _shaft, _flight, _fshape = 2;

        private bool _fetching;

        // 読み込みをやり直した回数
        private int _retries;

        // 台帳の名前と、いまの表示名が食い違っているか（名前を変えた人）
        private bool _nameChanged;

        // 所持リスト。「0,3,17」形式のまま持って、必要なときに照合する
        private string _ownTip = "", _ownBarrel = "", _ownShaft = "", _ownFlight = "";

        // 価格表。既定価格と、個別価格（キーは「種別_値」）
        private int[] _priceDefault;
        private DataDictionary _priceOverride;

        // パーシスタンスのキー。他システムと衝突しないよう接頭辞を付ける
        private const string KeyNo = "mydarts_no";
        private const string KeyCoin = "mydarts_coin";
        private const string KeyTip = "mydarts_tip";
        private const string KeyBarrel = "mydarts_barrel";
        private const string KeyShaft = "mydarts_shaft";
        private const string KeyFlight = "mydarts_flight";
        private const string KeyFShape = "mydarts_fshape";

        // ---- 他のスクリプトから読む用 ----
        public bool IsFound() { return _found; }
        /// <summary>台帳に載っている名前と、いまの表示名が違うか</summary>
        public bool IsNameChanged() { return _nameChanged; }
        public string GetMyNumber() { return _myNumber; }
        public int GetMyCoin() { return _myCoin; }
        public int GetTip() { return _tip; }
        public int GetBarrel() { return _barrel; }
        public int GetShaft() { return _shaft; }
        public int GetFlight() { return _flight; }
        public int GetFlightShape() { return _fshape; }

        private void Start()
        {
            // 読み込みが終わるまで登録UIは出さない。
            // 登録済みの人が「登録」を触ってしまわないようにするため。
            ShowRegisterUI(false);

            if (statusPanel != null)
            {
                statusPanel.ShowInfo(
                    "Loading...",
                    "Getting your player data.\nPlease wait a moment.");
            }

            if (fetchOnStart)
            {
                // 起動直後は他の初期化と重なるので少し待つ
                SendCustomEventDelayedSeconds(nameof(Fetch), startDelay);
            }
        }

        /// <summary>台帳を取りにいく。ボタンからも呼べる</summary>
        public void Fetch()
        {
            if (_fetching)
            {
                if (debugPanel != null) debugPanel.Log("fetch: already running");
                return;
            }

            if (ledgerUrl == null || string.IsNullOrEmpty(ledgerUrl.Get()))
            {
                if (debugPanel != null) debugPanel.Log("fetch: ledger url is not set");
                return;
            }

            _fetching = true;

            if (debugPanel != null)
            {
                debugPanel.SetState("Loading ledger");
                debugPanel.Log("fetch: " + ledgerUrl.Get());
            }

            VRCStringDownloader.LoadUrl(ledgerUrl, this);
        }

        // ============================================================
        //  レスポンス
        // ============================================================
        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            _fetching = false;

            var body = result.Result;
            if (debugPanel != null)
            {
                debugPanel.SetState("Idle");
                debugPanel.Log("ledger loaded (" + body.Length + " chars)");
            }

            DataToken root;
            if (!VRCJson.TryDeserializeFromJson(body, out root))
            {
                Fail("Could not parse the ledger.");
                return;
            }

            if (root.TokenType != TokenType.DataDictionary)
            {
                Fail("Unexpected ledger format.");
                return;
            }

            // players の配列を取り出す
            DataToken playersToken;
            if (!root.DataDictionary.TryGetValue("players", out playersToken) ||
                playersToken.TokenType != TokenType.DataList)
            {
                Fail("The ledger has no player list.");
                return;
            }

            var players = playersToken.DataList;

            ReadPriceTable(root.DataDictionary);

            // 自分の名前を決める。実機では必ず取れる
            var local = Networking.LocalPlayer;
            _myName = Utilities.IsValid(local) ? local.displayName : "";

            if (string.IsNullOrEmpty(_myName))
            {
                Fail("Could not read your display name.");
                return;
            }

            // 自分の行を探す。
            //
            // 名前ではなく番号で照合する。
            // VRChatの表示名は本人が変更できてしまい、変えられた瞬間に
            // 名前照合では別人になってしまうため。
            // 番号はパーシスタンス（アカウントに紐づく）に控えてあるので、
            // 名前を変えても変わらない。
            //
            // 番号をまだ持っていない人（初回）だけ名前で探す。
            var savedNo = ReadSavedNumber();
            _nameChanged = false;

            for (var i = 0; i < players.Count; i++)
            {
                DataToken row;
                if (!players.TryGetValue(i, out row)) continue;
                if (row.TokenType != TokenType.DataDictionary) continue;

                var d = row.DataDictionary;
                var rowNo = ReadString(d, "i", "");
                var rowName = ReadString(d, "n", "");

                if (savedNo.Length > 0)
                {
                    if (rowNo != savedNo) continue;
                    // 台帳の名前と今の名前が違う＝名前を変えた人
                    if (rowName != _myName) _nameChanged = true;
                }
                else
                {
                    if (rowName != _myName) continue;
                }

                _found = true;
                _myNumber = rowNo.Length > 0 ? rowNo : "----";
                _myCoin = ReadInt(d, "c", 0);
                _tip = ReadInt(d, "t", 0);
                _barrel = ReadInt(d, "b", 0);
                _shaft = ReadInt(d, "s", 0);
                _flight = ReadInt(d, "f", 0);
                _fshape = ReadInt(d, "p", 2);

                _ownTip = ReadString(d, "ot", "");
                _ownBarrel = ReadString(d, "ob", "");
                _ownShaft = ReadString(d, "os", "");
                _ownFlight = ReadString(d, "of", "");
                break;
            }

            // 番号で見つからなかった場合の保険。
            // 台帳を作り直したなど、番号が消えている場合に名前で拾い直す。
            if (!_found && savedNo.Length > 0)
            {
                for (var i = 0; i < players.Count; i++)
                {
                    DataToken row;
                    if (!players.TryGetValue(i, out row)) continue;
                    if (row.TokenType != TokenType.DataDictionary) continue;

                    var d = row.DataDictionary;
                    if (ReadString(d, "n", "") != _myName) continue;

                    _found = true;
                    _myNumber = ReadString(d, "i", "----");
                    _myCoin = ReadInt(d, "c", 0);
                    _tip = ReadInt(d, "t", 0);
                    _barrel = ReadInt(d, "b", 0);
                    _shaft = ReadInt(d, "s", 0);
                    _flight = ReadInt(d, "f", 0);
                    _fshape = ReadInt(d, "p", 2);

                    _ownTip = ReadString(d, "ot", "");
                    _ownBarrel = ReadString(d, "ob", "");
                    _ownShaft = ReadString(d, "os", "");
                    _ownFlight = ReadString(d, "of", "");
                    break;
                }
            }

            if (!_found)
            {
                if (statusPanel != null)
                {
                    statusPanel.ShowInfo(
                        "MyDarts - Not Registered",
                        "Press \"Build Register URL\" to get started.");
                }
                if (debugPanel != null)
                {
                    debugPanel.Log("not registered: " + _myName + " (" + players.Count + " rows)");
                }
                ShowRegisterUI(true);
                return;
            }

            _retries = 0;

            Save();
            ShowRegisterUI(false);

            if (statusPanel != null)
            {
                statusPanel.ShowSuccess(
                    _nameChanged ? "Welcome back (new name)" : "Welcome back",
                    "No. " + _myNumber + "   Coins: " + _myCoin + "\n" +
                    "Tip " + _tip + " / Barrel " + _barrel +
                    " / Shaft " + _shaft + " / Flight " + _flight + "-" + _fshape);
            }

            if (debugPanel != null)
            {
                debugPanel.Log("restored no=" + _myNumber + " coin=" + _myCoin +
                               (_nameChanged ? " (display name changed)" : ""));
            }
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            _fetching = false;

            var err = (result != null && !string.IsNullOrEmpty(result.Error)) ? result.Error : "unknown";

            if (debugPanel != null)
            {
                debugPanel.SetState("Idle");
            }

            // やり直し → それでも駄目なら前回の値、の順で Fail が面倒を見る
            Fail("ledger load error: " + err);
        }

        // ============================================================
        //  価格と所持
        // ============================================================

        /// <summary>台帳JSONに載っている価格表を取り込む</summary>
        private void ReadPriceTable(DataDictionary root)
        {
            DataToken pr;
            if (!root.TryGetValue("pr", out pr) || pr.TokenType != TokenType.DataDictionary) return;

            var table = pr.DataDictionary;

            DataToken def;
            if (table.TryGetValue("d", out def) && def.TokenType == TokenType.DataList)
            {
                var list = def.DataList;
                _priceDefault = new int[list.Count];
                for (var i = 0; i < list.Count; i++)
                {
                    DataToken t;
                    if (!list.TryGetValue(i, out t)) continue;
                    if (t.TokenType == TokenType.Double) _priceDefault[i] = (int)t.Double;
                    else if (t.TokenType == TokenType.Int) _priceDefault[i] = t.Int;
                }
            }

            DataToken ov;
            if (table.TryGetValue("o", out ov) && ov.TokenType == TokenType.DataDictionary)
            {
                _priceOverride = ov.DataDictionary;
            }
        }

        /// <summary>その番号を買うときの値段。所持済みなら0</summary>
        public int GetPrice(int kind, int value)
        {
            if (IsOwned(kind, value)) return 0;

            // 個別価格が先。無ければ種別の既定価格
            if (_priceOverride != null)
            {
                DataToken t;
                if (_priceOverride.TryGetValue(kind + "_" + value, out t))
                {
                    if (t.TokenType == TokenType.Double) return (int)t.Double;
                    if (t.TokenType == TokenType.Int) return t.Int;
                }
            }

            if (_priceDefault != null && kind >= 0 && kind < _priceDefault.Length)
            {
                return _priceDefault[kind];
            }
            return 0;
        }

        /// <summary>もう持っているか</summary>
        public bool IsOwned(int kind, int value)
        {
            var list = OwnedListOf(kind);
            if (string.IsNullOrEmpty(list)) return false;

            // 「12」が「120」に部分一致しないよう、区切りごとに数値で比べる
            var n = 0;
            var has = false;

            for (var i = 0; i <= list.Length; i++)
            {
                var end = (i == list.Length);
                var c = end ? ',' : list[i];

                if (c >= '0' && c <= '9')
                {
                    n = n * 10 + (c - '0');
                    has = true;
                    continue;
                }

                if (has && n == value) return true;
                n = 0;
                has = false;
            }
            return false;
        }

        /// <summary>その種別を何個持っているか</summary>
        public int GetOwnedCount(int kind)
        {
            var list = OwnedListOf(kind);
            if (string.IsNullOrEmpty(list)) return 0;

            var count = 0;
            var has = false;

            for (var i = 0; i <= list.Length; i++)
            {
                var end = (i == list.Length);
                var c = end ? ',' : list[i];

                if (c >= '0' && c <= '9') { has = true; continue; }
                if (has) count++;
                has = false;
            }
            return count;
        }

        /// <summary>
        /// 所持している番号を「0,3,17」の形で返す。
        /// 中身は台帳の J〜M 列そのもの。コレクション画面が使う。
        /// </summary>
        public string GetOwnedList(int kind)
        {
            return OwnedListOf(kind);
        }

        private string OwnedListOf(int kind)
        {
            if (kind == 1) return _ownTip;
            if (kind == 2) return _ownBarrel;
            if (kind == 3) return _ownShaft;
            if (kind == 4) return _ownFlight;
            return "";
        }

        /// <summary>買った直後に装備中の番号を更新する</summary>
        public void SetEquipped(int kind, int value)
        {
            if (kind == 1) _tip = value;
            else if (kind == 2) _barrel = value;
            else if (kind == 3) _shaft = value;
            else if (kind == 4) _flight = value;
            else if (kind == 5) _fshape = value;

            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;

            if (kind == 1) PlayerData.SetInt(KeyTip, value);
            else if (kind == 2) PlayerData.SetInt(KeyBarrel, value);
            else if (kind == 3) PlayerData.SetInt(KeyShaft, value);
            else if (kind == 4) PlayerData.SetInt(KeyFlight, value);
            else if (kind == 5) PlayerData.SetInt(KeyFShape, value);
        }

        /// <summary>買った直後にワールド側の所持リストへ追加する</summary>
        public void MarkOwned(int kind, int value)
        {
            if (IsOwned(kind, value)) return;

            var add = (value).ToString();
            if (kind == 1) _ownTip = Append(_ownTip, add);
            else if (kind == 2) _ownBarrel = Append(_ownBarrel, add);
            else if (kind == 3) _ownShaft = Append(_ownShaft, add);
            else if (kind == 4) _ownFlight = Append(_ownFlight, add);
        }

        private string Append(string list, string add)
        {
            return string.IsNullOrEmpty(list) ? add : (list + "," + add);
        }

        // ============================================================
        //  パーシスタンス
        // ============================================================
        private void Save()
        {
            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;

            // Persistence は VRChat 実機でのみ動く。エディタでは何も起きない
            PlayerData.SetString(KeyNo, _myNumber);
            PlayerData.SetInt(KeyCoin, _myCoin);
            PlayerData.SetInt(KeyTip, _tip);
            PlayerData.SetInt(KeyBarrel, _barrel);
            PlayerData.SetInt(KeyShaft, _shaft);
            PlayerData.SetInt(KeyFlight, _flight);
            PlayerData.SetInt(KeyFShape, _fshape);
        }

        /// <summary>
        /// 登録が通った直後に、その内容をそのまま取り込む。
        ///
        /// 台帳を読み直すと5秒以上かかるうえ、
        /// 書き込んだ直後だと反映が間に合わないことがある。
        /// 登録の返事に必要な値が全部入っているので、それを使う。
        /// </summary>
        public void ApplyRegistration(string no, int coin,
                                      int tip, int barrel, int shaft, int flight, int fshape,
                                      string ownTip, string ownBarrel, string ownShaft, string ownFlight)
        {
            _found = true;
            _myNumber = string.IsNullOrEmpty(no) ? "----" : no;
            _myCoin = coin;
            _tip = tip;
            _barrel = barrel;
            _shaft = shaft;
            _flight = flight;
            _fshape = fshape;

            // 所持リストが返っていなければ、いま着けているものだけ持っている扱いにする。
            // 登録時に配られたパーツはサーバー側でも所持済みになっている。
            _ownTip = string.IsNullOrEmpty(ownTip) ? tip.ToString() : ownTip;
            _ownBarrel = string.IsNullOrEmpty(ownBarrel) ? barrel.ToString() : ownBarrel;
            _ownShaft = string.IsNullOrEmpty(ownShaft) ? shaft.ToString() : ownShaft;
            _ownFlight = string.IsNullOrEmpty(ownFlight) ? flight.ToString() : ownFlight;

            _retries = 0;
            _nameChanged = false;

            Save();
            ShowRegisterUI(false);

            if (debugPanel != null)
            {
                debugPanel.Log("registered -> no=" + _myNumber + " coin=" + _myCoin);
            }
        }

        /// <summary>
        /// 前に保存した自分の番号を読む。無ければ空文字。
        /// これはアカウントに紐づくので、表示名を変えても消えない。
        /// </summary>
        private string ReadSavedNumber()
        {
            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return "";
            if (!PlayerData.HasKey(local, KeyNo)) return "";

            var no = PlayerData.GetString(local, KeyNo);
            if (string.IsNullOrEmpty(no)) return "";
            if (no == "----") return "";
            return no;
        }

        /// <summary>前回の値を読む。あれば true</summary>
        private bool LoadCache()
        {
            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return false;
            if (!PlayerData.HasKey(local, KeyNo)) return false;

            _myNumber = PlayerData.GetString(local, KeyNo);
            if (string.IsNullOrEmpty(_myNumber)) return false;

            _myCoin = PlayerData.GetInt(local, KeyCoin);
            _tip = PlayerData.GetInt(local, KeyTip);
            _barrel = PlayerData.GetInt(local, KeyBarrel);
            _shaft = PlayerData.GetInt(local, KeyShaft);
            _flight = PlayerData.GetInt(local, KeyFlight);
            _fshape = PlayerData.GetInt(local, KeyFShape);
            _found = true;
            return true;
        }

        // ============================================================
        //  小物
        // ============================================================
        private void ShowRegisterUI(bool show)
        {
            if (registerGroup != null && registerGroup.activeSelf != show)
            {
                registerGroup.SetActive(show);
            }
        }

        /// <summary>
        /// 読み込みに失敗したときの後始末。
        ///
        /// サーバーの入れ替え中などに、JSONではなくエラーページが返ることがある。
        /// 一度きりで諦めず、少し待ってから取り直す。
        /// ダウンロードは5秒に1回までなので、間隔はそれより長くしてある。
        /// </summary>
        private void Fail(string message)
        {
            if (debugPanel != null) debugPanel.Log(message);

            if (_retries < maxRetry)
            {
                _retries++;

                if (statusPanel != null)
                {
                    statusPanel.ShowInfo(
                        "Loading...",
                        "Retrying (" + _retries + "/" + maxRetry + ")");
                }
                if (debugPanel != null)
                {
                    debugPanel.Log("retry ledger in " + retryDelay + "s (" + _retries + "/" + maxRetry + ")");
                }

                SendCustomEventDelayedSeconds(nameof(Fetch), retryDelay);
                return;
            }

            // 何度やっても駄目なら、前回の値で動かす
            if (LoadCache())
            {
                ShowRegisterUI(false);
                if (statusPanel != null)
                {
                    statusPanel.ShowInfo(
                        "Offline",
                        "Could not read the ledger. Using your last saved data.\n" +
                        "No. " + _myNumber + "   Coins: " + _myCoin);
                }
                return;
            }

            if (statusPanel != null) statusPanel.ShowError("Ledger Error", message);
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

            // 番号は "0001" のように文字列で来ることがある
            if (t.TokenType == TokenType.String)
            {
                var s = t.String;
                var n = 0;
                var any = false;
                for (var i = 0; i < s.Length; i++)
                {
                    var c = s[i];
                    if (c < '0' || c > '9') continue;
                    n = n * 10 + (c - '0');
                    any = true;
                }
                if (any) return n;
            }
            return fallback;
        }
    }
}
