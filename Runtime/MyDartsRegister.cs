using UdonSharp;
using UnityEngine;
using TMPro;
using VRC.SDKBase;
using VRC.SDK3.Data;
using VRC.SDK3.Components;
using VRC.SDK3.StringLoading;

namespace MyDartsSystem
{
    /// <summary>
    /// マイダーツの初回登録。
    ///
    /// VRChatはUdonから任意のURLを作れないので、
    ///   1. ワールドが完成URLを「ただの文字列」として表示する
    ///   2. プレイヤーがコピーして VRCUrlInputField に貼る
    ///   3. 貼られたURLを LoadUrl で叩く
    /// という流れになる。登録は生涯1回だけ。
    ///
    /// GASのレスポンスはそのまま読めるので、結果はここで拾って表示する。
    ///
    /// ※ ワールド内に出す文字列だけは、フォントの都合で英語にしてある。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsRegister : UdonSharpBehaviour
    {
        [Header("=== GAS ===")]
        [SerializeField, Tooltip("GASウェブアプリのURL。末尾は /exec")]
        private string gasBaseUrl = "https://script.google.com/macros/s/XXXXXXXX/exec";

        [Header("=== UI 参照 ===")]
        [SerializeField, Tooltip("組み立てたURLを表示する欄。プレイヤーがここからコピーする")]
        private TMP_InputField urlOutputField;

        [SerializeField, Tooltip("プレイヤーがURLを貼り付ける欄")]
        private VRCUrlInputField urlInputField;

        [SerializeField, Tooltip("プレイヤー向けの状況パネル")]
        private MyDartsStatusPanel statusPanel;

        [SerializeField, Tooltip("開発用のログパネル")]
        private MyDartsDebugPanel debugPanel;

        [SerializeField, Tooltip("登録UIをまとめた入れ物。登録が済んだら隠す")]
        private GameObject registerGroup;

        [SerializeField, Tooltip("登録が通ったら、その内容をここへ渡す")]
        private MyDartsFetcher fetcher;

        [SerializeField, Tooltip("登録が通ったら、名乗り直させる")]
        private MyDartsSender sender;

        [Header("=== 登録時の初期パーツ ===")]
        [SerializeField, Range(0, 199)] private int initialTip = 0;
        [SerializeField, Range(0, 199)] private int initialBarrel = 0;
        [SerializeField, Range(0, 199)] private int initialShaft = 0;
        [SerializeField, Range(0, 199)] private int initialFlight = 0;
        [SerializeField, Range(0, 3), Tooltip("0:Kite 1:Slim 2:Standard 3:Tear")]
        private int initialFlightShape = 2;

        [Header("=== エディタ検証用 ===")]
        [SerializeField, Tooltip("プレイモードで LocalPlayer が取れないときに使う仮の名前。VRChat実機では使われない")]
        private string editorTestName = "EditorTester";

        // 登録が済んだあとに保持する情報
        private bool _registered;
        private string _myNumber = "";
        private int _myCoin;

        // 二重送信よけ
        private bool _sending;

        public bool IsRegistered() { return _registered; }
        public string GetMyNumber() { return _myNumber; }
        public int GetMyCoin() { return _myCoin; }

        private void Start()
        {
            if (statusPanel != null)
            {
                statusPanel.ShowInfo(
                    "MyDarts - Not Registered",
                    "Press \"Build Register URL\" to get started.");
            }
            if (debugPanel != null)
            {
                debugPanel.SetState("Idle");
                debugPanel.Log("MyDartsRegister started");
            }
        }

        // ============================================================
        //  ① 登録用URLを組み立てて表示する（ボタンから呼ぶ）
        // ============================================================
        public void OnClickBuildUrl()
        {
            // 実機では LocalPlayer が必ず取れる。
            // エディタのプレイモード（ClientSim無効など）では null になるので、
            // その場合だけ検証用の仮名でURLを組み立てて動作確認できるようにする。
            var local = Networking.LocalPlayer;
            var name = Utilities.IsValid(local) ? local.displayName : "";

            if (string.IsNullOrEmpty(name))
            {
                name = editorTestName;
                if (debugPanel != null)
                {
                    debugPanel.Log("LocalPlayer not available - using test name: " + name);
                }
            }

            if (string.IsNullOrEmpty(name))
            {
                Report("Could not read your display name.", true);
                return;
            }

            var url =
                gasBaseUrl +
                "?r=" + Encode(name) +
                "&t=" + initialTip +
                "&bl=" + initialBarrel +
                "&s=" + initialShaft +
                "&f=" + initialFlight +
                "&fs=" + initialFlightShape;

            if (urlOutputField != null)
            {
                urlOutputField.text = url;
            }

            if (statusPanel != null)
            {
                statusPanel.ShowInfo(
                    "URL Ready",
                    "Copy the URL above and paste it into the field below.\nYou only need to do this once.");
            }

            if (debugPanel != null)
            {
                debugPanel.SetState("URL built / waiting for paste");
                debugPanel.Log("Built URL: " + url);
            }
        }

        // ============================================================
        //  ② 貼り付けられたURLを送信する
        //     VRCUrlInputField の OnEndEdit から呼ぶ
        // ============================================================
        public void OnSubmitUrl()
        {
            if (_sending)
            {
                Report("Already sending. Please wait a moment.", false);
                return;
            }

            if (urlInputField == null)
            {
                Report("The URL input field is not assigned.", true);
                return;
            }

            var url = urlInputField.GetUrl();
            if (url == null || string.IsNullOrEmpty(url.Get()))
            {
                Report("The URL field is empty.", true);
                return;
            }

            _sending = true;

            if (statusPanel != null)
            {
                statusPanel.ShowInfo("Sending...", "Please wait.");
            }
            if (debugPanel != null)
            {
                debugPanel.SetState("Sending");
                debugPanel.Log("LoadUrl: " + url.Get());
            }

            VRCStringDownloader.LoadUrl(url, this);
        }

        // ============================================================
        //  レスポンス
        // ============================================================
        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            _sending = false;

            var body = result.Result;
            if (debugPanel != null)
            {
                debugPanel.SetState("Idle");
                debugPanel.Log("Response: " + body);
            }

            DataToken root;
            if (!VRCJson.TryDeserializeFromJson(body, out root))
            {
                Report("Could not parse the response.", true);
                return;
            }

            if (root.TokenType != TokenType.DataDictionary)
            {
                Report("Unexpected response format.", true);
                return;
            }

            var dict = root.DataDictionary;

            DataToken okToken;
            var ok = dict.TryGetValue("ok", out okToken)
                     && okToken.TokenType == TokenType.Boolean
                     && okToken.Boolean;

            if (!ok)
            {
                DataToken errToken;
                var err = dict.TryGetValue("err", out errToken) && errToken.TokenType == TokenType.String
                    ? errToken.String
                    : "UNKNOWN";

                if (statusPanel != null)
                {
                    statusPanel.ShowError("Registration Failed", ErrorMessage(err));
                }
                if (debugPanel != null)
                {
                    debugPanel.Log("Error: " + err);
                }
                return;
            }

            // --- 成功 ---
            _registered = true;
            _myNumber = ReadString(dict, "no", "----");
            _myCoin = ReadInt(dict, "coin", 0);

            var already = false;
            DataToken alreadyToken;
            if (dict.TryGetValue("already", out alreadyToken) && alreadyToken.TokenType == TokenType.Boolean)
            {
                already = alreadyToken.Boolean;
            }

            if (statusPanel != null)
            {
                if (already)
                {
                    statusPanel.ShowSuccess(
                        "Already Registered",
                        "Coins: " + _myCoin);
                }
                else
                {
                    statusPanel.ShowSuccess(
                        "Registration Complete",
                        "Welcome. Coins: " + _myCoin);
                }
            }

            if (debugPanel != null)
            {
                debugPanel.Log("Register OK no=" + _myNumber + " coin=" + _myCoin + " already=" + already);
            }

            // 読み込み側へ渡す。
            // これをしないと、台帳を読み直すまで「未登録」のままになり、
            // コインもガチャも動かない。
            if (fetcher != null)
            {
                fetcher.ApplyRegistration(
                    _myNumber,
                    _myCoin,
                    ReadInt(dict, "tip", initialTip),
                    ReadInt(dict, "barrel", initialBarrel),
                    ReadInt(dict, "shaft", initialShaft),
                    ReadInt(dict, "flight", initialFlight),
                    ReadInt(dict, "fshape", initialFlightShape),
                    ReadString(dict, "ot", ""),
                    ReadString(dict, "ob", ""),
                    ReadString(dict, "os", ""),
                    ReadString(dict, "of", ""));
            }

            // 番号と残高が決まったので、送信側にも渡して名乗り直させる
            if (sender != null)
            {
                sender.AdoptCoin(_myCoin);
                sender.RetryBind();
            }

            // 登録が通ったらもう出す必要がない
            if (registerGroup != null && registerGroup.activeSelf)
            {
                registerGroup.SetActive(false);
            }
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            _sending = false;

            // 原因を決めつけず、SDKが返したエラーをそのまま出す。
            var err = (result != null && !string.IsNullOrEmpty(result.Error)) ? result.Error : "unknown";

            if (statusPanel != null)
            {
                statusPanel.ShowError(
                    "Send Failed",
                    err + "\n\nIf this says the URL is blocked, turn on \"Allow Untrusted URLs\" in VRChat settings.");
            }
            if (debugPanel != null)
            {
                debugPanel.SetState("Idle");
                debugPanel.Log("LoadUrl error: " + err);
                if (urlInputField != null)
                {
                    var u = urlInputField.GetUrl();
                    debugPanel.Log("sent url: " + (u == null ? "(null)" : u.Get()));
                }
            }
        }

        // ============================================================
        //  小物
        // ============================================================
        private void Report(string message, bool isError)
        {
            if (statusPanel != null)
            {
                if (isError) statusPanel.ShowError("Error", message);
                else statusPanel.ShowInfo("Please Wait", message);
            }
            if (debugPanel != null)
            {
                debugPanel.Log(message);
            }
        }

        private string ErrorMessage(string code)
        {
            if (code == "FULL") return "All registration slots are taken. Please contact the world owner.";
            if (code == "EMPTY_NAME") return "Could not read your display name.";
            if (code == "NAME_TOO_LONG") return "Your display name is too long.";
            if (code == "LOCK_TIMEOUT") return "The server is busy. Please wait and try again.";
            return "Error code: " + code;
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

        /// <summary>
        /// UdonにはSystem.Webが無いので、URLに載せられない文字を自前で%エンコードする。
        /// 未予約文字（英数字と - _ . ~）だけそのまま通す。
        /// </summary>
        private string Encode(string src)
        {
            if (string.IsNullOrEmpty(src)) return "";

            var hex = "0123456789ABCDEF";
            var result = "";

            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];

                var safe =
                    (c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c == '-' || c == '_' || c == '.' || c == '~';

                if (safe)
                {
                    result += c;
                    continue;
                }

                // UTF-8のバイト列に落としてから%XXにする
                var code = (int)c;

                if (code < 0x80)
                {
                    result += "%" + hex[(code >> 4) & 0xF] + hex[code & 0xF];
                }
                else if (code < 0x800)
                {
                    var b1 = 0xC0 | (code >> 6);
                    var b2 = 0x80 | (code & 0x3F);
                    result += "%" + hex[(b1 >> 4) & 0xF] + hex[b1 & 0xF];
                    result += "%" + hex[(b2 >> 4) & 0xF] + hex[b2 & 0xF];
                }
                else
                {
                    var b1 = 0xE0 | (code >> 12);
                    var b2 = 0x80 | ((code >> 6) & 0x3F);
                    var b3 = 0x80 | (code & 0x3F);
                    result += "%" + hex[(b1 >> 4) & 0xF] + hex[b1 & 0xF];
                    result += "%" + hex[(b2 >> 4) & 0xF] + hex[b2 & 0xF];
                    result += "%" + hex[(b3 >> 4) & 0xF] + hex[b3 & 0xF];
                }
            }

            return result;
        }
    }
}
