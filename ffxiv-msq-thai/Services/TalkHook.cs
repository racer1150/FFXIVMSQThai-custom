using System;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FfxivMsqThai.Services;

public sealed class TalkHook : IDisposable
{
    private const string AddonName = "Talk";

    private static readonly string[] CutsceneAddons = {
        "TalkSubtitle",
        "CutSceneSubtitle",
        "CutsceneDialogue"
    };

    public string ActiveAddonName { get; private set; } = "Talk";

    private static readonly Regex DashOnlyLine =
        new(@"^[\-–—\s]+$", RegexOptions.Compiled);
    private static readonly Regex InlineDashRun =
        new(@"[\-–—]{3,}", RegexOptions.Compiled);
    private static readonly Regex SeControl =
        new(@"[\x02][\s\S]{1,4}[\x03]|[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]",
            RegexOptions.Compiled);

    // ── Player-name placeholders ──────────────────────────────────────────
    // ไฟล์คำแปลใช้ Forename / Surname เป็นตัวแทนชื่อผู้เล่น (บางที่ครอบด้วย [] {} <>)
    // ต้องแทนกลับเป็นชื่อตัวละครจริงก่อนแสดงผล
    // ใช้ lookaround แบบ Latin แทน \b เพราะอักษรไทยนับเป็น \w ทำให้ \b ไม่ทำงานเมื่อชื่ออยู่ติดตัวอักษรไทย
    private static Regex PlaceholderRegex(string inner) => new(
        $@"(?:\[\s*{inner}\s*\]|\{{\s*{inner}\s*\}}|<\s*{inner}\s*>|(?<![A-Za-z]){inner}(?![A-Za-z]))",
        RegexOptions.Compiled);

    private static readonly Regex PhFullName      = PlaceholderRegex(@"Forename\s+Surname");
    private static readonly Regex PhFullNameRev   = PlaceholderRegex(@"Surname\s+Forename");
    private static readonly Regex PhForename      = PlaceholderRegex("Forename");
    private static readonly Regex PhSurname       = PlaceholderRegex("Surname");

    private readonly IAddonLifecycle    _addonLifecycle;
    private readonly DialogueDictionary _dictionary;
    private readonly IClientState       _clientState;
    private readonly IObjectTable       _objectTable;
    private readonly IPluginLog         _log = Plugin.Log;

    // เก็บชื่อล่าสุดไว้ เผื่อบางจังหวะ (เช่น ระหว่างโหลดฉาก) LocalPlayer เป็น null
    private string     _playerFirst   = string.Empty;
    private string     _playerLast    = string.Empty;

    private string     _lastTextEn    = string.Empty;
    private string     _lastAddonName = string.Empty;

    public string[] CurrentTokens { get; private set; } = Array.Empty<string>();

    public TalkHook(
        IAddonLifecycle    addonLifecycle,
        DialogueDictionary dictionary,
        IClientState       clientState,
        IObjectTable       objectTable)
    {
        _addonLifecycle = addonLifecycle;
        _dictionary     = dictionary;
        _clientState    = clientState;
        _objectTable    = objectTable;

        _addonLifecycle.RegisterListener(AddonEvent.PreRefresh,  AddonName, OnPreRefresh);
        _addonLifecycle.RegisterListener(AddonEvent.PreHide,     AddonName, OnHide);
        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnHide);

        foreach (var addon in CutsceneAddons)
        {
            _addonLifecycle.RegisterListener(AddonEvent.PreSetup,    addon, OnPreRefresh);
            _addonLifecycle.RegisterListener(AddonEvent.PreRefresh,  addon, OnPreRefresh);
            _addonLifecycle.RegisterListener(AddonEvent.PreHide,     addon, OnHide);
            _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, addon, OnHide);
        }
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PreRefresh,  AddonName, OnPreRefresh);
        _addonLifecycle.UnregisterListener(AddonEvent.PreHide,     AddonName, OnHide);
        _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnHide);

        foreach (var addon in CutsceneAddons)
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PreSetup,    addon, OnPreRefresh);
            _addonLifecycle.UnregisterListener(AddonEvent.PreRefresh,  addon, OnPreRefresh);
            _addonLifecycle.UnregisterListener(AddonEvent.PreHide,     addon, OnHide);
            _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addon, OnHide);
        }
    }

    private unsafe void OnPreRefresh(AddonEvent type, AddonArgs args)
    {
        ActiveAddonName = args.AddonName;

        // ── Read dialogue text ────────────────────────────────────────────
        string textEn;

        if (args.AddonName == AddonName)
        {
            if (args is not AddonRefreshArgs refreshArgs) return;
            var atkValues = (AtkValue*)refreshArgs.AtkValues;
            if (atkValues == null || refreshArgs.AtkValueCount < 1) return;

            var textPtr = (nint)atkValues[0].String.Value;
            if (textPtr == 0) { CurrentTokens = Array.Empty<string>(); return; }

            textEn = MemoryHelper.ReadSeStringAsString(out _, textPtr);
        }
        else if (args.AddonName is "TalkSubtitle" or "CutSceneSubtitle" or "CutsceneDialogue")
        {
            AtkValue* atkValues = args switch
            {
                AddonSetupArgs   s => (AtkValue*)s.AtkValues,
                AddonRefreshArgs r => (AtkValue*)r.AtkValues,
                _                  => null
            };

            if (atkValues != null
                && atkValues[0].Type == AtkValueType.String
                && atkValues[0].String.Value != null)
                textEn = MemoryHelper.ReadSeStringAsString(out _, (nint)atkValues[0].String.Value);
            else
                textEn = GetTextFromSubtitleAddon((AtkUnitBase*)args.Addon.Address);
        }
        else return;

        if (textEn == _lastTextEn && args.AddonName == _lastAddonName) return;
        _lastTextEn    = textEn;
        _lastAddonName = args.AddonName;

        if (string.IsNullOrWhiteSpace(textEn)) { CurrentTokens = Array.Empty<string>(); return; }

        // ── Normalize key ─────────────────────────────────────────────────
        var displayEn = DialogueDictionary.NormalizeEnglishKey(textEn);
        UpdatePlayerName();

        if (_playerFirst.Length > 0)
        {
            var fullName = _playerLast.Length > 0 ? $"{_playerFirst} {_playerLast}" : _playerFirst;
            displayEn = ReplaceName(displayEn, fullName,      "Forename Surname");
            displayEn = ReplaceName(displayEn, _playerFirst,  "Forename");
            if (_playerLast.Length > 0)
                displayEn = ReplaceName(displayEn, _playerLast, "Surname");
        }

        if (displayEn.Length == 0) { CurrentTokens = Array.Empty<string>(); return; }

        _log.Information($"[MSQ-Thai] Lookup '{Clip(displayEn)}'");

        // ── Unified Search ───────────────────
        var translation = _dictionary.GetTranslation(displayEn);

        if (translation != null)
        {
            _log.Information($"[MSQ-Thai] HIT");
            ApplyTranslation(translation);
        }
        else
        {
            _log.Information($"[MSQ-Thai] MISS");
            CurrentTokens = Array.Empty<string>();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void OnHide(AddonEvent type, AddonArgs args)
    {
        CurrentTokens  = Array.Empty<string>();
        _lastTextEn    = string.Empty;
        _lastAddonName = string.Empty;
    }

    private void ApplyTranslation(string rawThai)
    {
        var clean = SanitizeThai(ReplacePlaceholders(rawThai, _playerFirst, _playerLast));
        CurrentTokens = string.IsNullOrWhiteSpace(clean)
            ? Array.Empty<string>()
            : ThaiWordSegmenter.Segment(clean);
    }

    private void UpdatePlayerName()
    {
        var name = _objectTable.LocalPlayer?.Name.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;

        var parts = name.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        _playerFirst = parts[0];
        _playerLast  = parts.Length > 1 ? parts[1] : string.Empty;
    }

    /// <summary>แทนที่ชื่อผู้เล่นในข้อความอังกฤษ (ฝั่งคีย์ค้นหา) โดยไม่ไปกัดกลางคำอื่น เช่น ชื่อ "Al" ใน "Alisaie"</summary>
    private static string ReplaceName(string text, string name, string placeholder)
    {
        if (string.IsNullOrEmpty(name)) return text;
        return Regex.Replace(
            text,
            $@"(?<![A-Za-z]){Regex.Escape(name)}(?![A-Za-z])",
            _ => placeholder);
    }

    /// <summary>
    /// แทน Forename / Surname (และรูปแบบ [Forename] {Forename} &lt;Forename&gt; Forename Surname)
    /// ในคำแปลไทยด้วยชื่อตัวละครจริง ถ้ายังไม่รู้ชื่อจะคืนข้อความเดิม
    /// </summary>
    internal static string ReplacePlaceholders(string text, string first, string last)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(first)) return text;

        var full = string.IsNullOrEmpty(last) ? first : $"{first} {last}";

        // ลำดับสำคัญ: รูปแบบยาวก่อนรูปแบบสั้น
        text = PhFullName.Replace(text,    _ => full);
        text = PhFullNameRev.Replace(text, _ => string.IsNullOrEmpty(last) ? first : $"{last} {first}");
        text = PhForename.Replace(text,    _ => first);
        text = PhSurname.Replace(text,     _ => string.IsNullOrEmpty(last) ? first : last);
        return text;
    }

    private static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;

    internal static string SanitizeThai(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (DashOnlyLine.IsMatch(text)) return string.Empty;
        text = SeControl.Replace(text, string.Empty);
        text = InlineDashRun.Replace(text, string.Empty);
        return text.Trim();
    }

    private unsafe string GetTextFromSubtitleAddon(AtkUnitBase* addon)
    {
        if (addon == null) return string.Empty;
        var textNode = FindTextNode(addon->RootNode);
        if (textNode == null) return string.Empty;

        var strPtr = ((AtkTextNode*)textNode)->NodeText.StringPtr.Value;
        if (strPtr == null) return string.Empty;

        return MemoryHelper.ReadSeStringAsString(out _, (nint)strPtr);
    }

    private unsafe AtkResNode* FindTextNode(AtkResNode* node)
    {
        if (node == null) return null;
        if ((int)node->Type == 3) return node;
        var child = FindTextNode(node->ChildNode);
        if (child != null) return child;
        return FindTextNode(node->NextSiblingNode);
    }
}
