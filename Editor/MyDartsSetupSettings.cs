#if UNITY_EDITOR
using UnityEngine;

namespace MyDartsSystem.EditorTools
{
    /// <summary>
    /// セットアップの設定を保存しておくアセット。
    ///
    /// これがあるおかげで、生成物を丸ごと作り直しても
    /// GASのURLや初期パーツ番号を入れ直さなくて済む。
    /// 場所: Assets/MyDartsSystem/MyDartsSetupSettings.asset
    /// </summary>
    public class MyDartsSetupSettings : ScriptableObject
    {
        [Header("=== GAS ===")]
        [Tooltip("GASウェブアプリのURL。末尾は /exec")]
        public string gasBaseUrl = "https://script.google.com/macros/s/XXXXXXXX/exec";

        [Tooltip("台帳JSONの取得先。空なら送信先URL + ?all=1 を使う")]
        public string ledgerUrl = "";

        [Header("=== ガチャ演出の動画 ===")]
        [Tooltip("直リンクの mp4 を推奨。空なら演出は動画なしで進む")]
        public string gachaVideoUrl = "";

        [Header("=== 登録時の初期パーツ ===")]
        [Range(0, 199)] public int initialTip = 0;
        [Range(0, 199)] public int initialBarrel = 0;
        [Range(0, 199)] public int initialShaft = 0;
        [Range(0, 199)] public int initialFlight = 0;
        [Range(0, 3), Tooltip("0:Kite 1:Slim 2:Standard 3:Tear")]
        public int initialFlightShape = 2;

        [Header("=== 配置 ===")]
        [Tooltip("プレイヤー向けパネルの位置")]
        public Vector3 playerPanelPosition = new Vector3(0f, 1.5f, 2f);

        [Tooltip("デバッグパネルの位置（プレイヤーから見えない場所に）")]
        public Vector3 debugPanelPosition = new Vector3(5f, 1.5f, 2f);

        [Tooltip("パネルの大きさ（メートル）")]
        public Vector2 panelSizeMeters = new Vector2(1.2f, 0.9f);

        [Tooltip("筐体（プレイヤーが触る画面）の大きさ（メートル）。\n" +
                 "ゲームセンターの機械のように縦長にしてある。")]
        public Vector2 cabinetSizeMeters = new Vector2(1.2f, 1.6f);

        [Header("=== デバッグパネル ===")]
        [Tooltip("生成時にデバッグパネルを非アクティブにする")]
        public bool debugPanelHiddenByDefault = false;

        [Range(5, 60)] public int debugMaxLines = 20;
    }
}
#endif
