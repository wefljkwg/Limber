using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Koi.Props;

namespace Limber.UI;

/// <summary>
/// Koikatsu > Import props: Koikatsu's studio items listed from its mods folder; the ones ticked are converted into
/// solid game models in the "Limber Koikatsu Props" Penumbra mod, to place from Koikatsu > Props.
/// </summary>
public sealed class KoiImportTab
{
    private readonly RigSettings _config;
    private string _search = "";
    private string _pack = "";
    private readonly HashSet<KoiItem> _ticked = new();
    private Limber.BodyRig.OneClickRig.Progress? _progress;

    public KoiImportTab(RigSettings config)
    {
        _config = config;
        KoiProps.LoadCached();
    }

    public void Draw()
    {
        ImGui.TextWrapped("Koikatsu's studio items (from its sideloader mods) made into solid game models: each one's meshes as they rest, "
            + "with their main textures and colours. They go into a mod for your mod manager, \"" + KoiProps.ModName + "\", and you place them from Koikatsu > Props "
            + "(move, turn, size, attach to a bone, like housing items).");
        string folder = _config.KoikatsuFolder ?? "";
        ImGui.SetNextItemWidth(-120 * ImGuiHelpers.GlobalScale);
        if(ImGui.InputTextWithHint("###koi_folder", "Koikatsu's folder (the one with Koikatu.exe)", ref folder, 400))
        {
            _config.KoikatsuFolder = folder;
            _config.Save();
        }
        ImGui.SameLine();
        using(ImRaii.Disabled(folder.Trim().Length == 0 || KoiProps.Scanning))
        {
            if(ImGui.Button(KoiProps.Items == null ? "Read the mods###koi_scan" : "Read again###koi_scan"))
                KoiProps.Scan(folder.Trim().Trim('"'));
        }
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Looks through every mod in its \"mods\" folder for studio items. The first time takes a while; after that only changed mods are read again.");
        if(KoiProps.ScanStatus.Length > 0)
            ImGui.TextDisabled(KoiProps.ScanStatus);

        var items = KoiProps.Items;
        if(items == null || items.Count == 0)
        {
            if(!KoiProps.Scanning)
                ImGui.TextDisabled("Not read yet. Put in Koikatsu's folder and press \"Read the mods\".");
            DrawProgress();
            return;
        }

        // Filter.
        var packs = items.Select(i => i.Pack).Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Mod###koi_pack", _pack.Length == 0 ? $"(all {packs.Count})" : _pack))
        {
            if(combo.Success)
            {
                if(ImGui.Selectable("(all)###koi_pack_all", _pack.Length == 0))
                    _pack = "";
                foreach(var p in packs)
                    if(ImGui.Selectable($"{p}###koi_pack_{p}", p == _pack))
                        _pack = p;
            }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###koi_search", "Search by name, category or mod", ref _search, 80);
        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = items.Where(i => (_pack.Length == 0 || i.Pack == _pack)
            && words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Category.Contains(w, StringComparison.OrdinalIgnoreCase)
                || i.Pack.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(2000).ToList();
        var imported = new HashSet<string>(KoiProps.Imported.Select(p => p.Key));
        if(ImGui.SmallButton("Tick all shown###koi_tick_all"))
            foreach(var i in shown)
                _ticked.Add(i);
        ImGui.SameLine();
        if(ImGui.SmallButton("Untick all###koi_untick"))
            _ticked.Clear();
        ImGui.SameLine();
        ImGui.TextDisabled($"{shown.Count}{(shown.Count == 2000 ? "+" : "")} shown, {_ticked.Count} ticked, {KoiProps.Imported.Count} converted so far.");
        using(var child = ImRaii.Child("###koi_list", new Vector2(-1, 300 * ImGuiHelpers.GlobalScale), true))
        {
            if(child.Success)
                foreach(var i in shown)
                {
                    bool on = _ticked.Contains(i);
                    string label = $"{(i.Name.Length > 0 ? i.Name : i.Asset)}{(imported.Contains(i.Key) ? "  (converted)" : "")}###koi_i_{i.Zip.GetHashCode()}_{i.Id}_{i.Asset}";
                    if(ImGui.Checkbox(label, ref on))
                    {
                        if(on)
                            _ticked.Add(i);
                        else
                            _ticked.Remove(i);
                    }
                    if(ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{i.Pack}{(i.Category.Length > 0 ? " > " + i.Category : "")}\n#{i.Id}, {i.Bundle} ({i.Asset})");
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X * 0.6f);
                    ImGui.TextDisabled(i.Category.Length > 0 ? i.Category : i.Pack);
                }
        }

        int size = _config.KoiPropsMaxTexture;
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        using(var combo = ImRaii.Combo("Biggest texture###koi_tex", $"{size}"))
        {
            if(combo.Success)
                foreach(var s in new[] { 256, 512, 1024, 2048, 4096 })
                    if(ImGui.Selectable($"{s}###koi_tex_{s}", s == size))
                    {
                        _config.KoiPropsMaxTexture = s;
                        _config.Save();
                    }
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Bigger textures are made smaller to this (in pixels across): smaller files, less memory.");
        ImGui.SameLine();
        bool busy = _progress != null && !_progress.Done;
        using(ImRaii.Disabled(_ticked.Count == 0 || busy))
        {
            if(ImGui.Button($"Convert {_ticked.Count} into the mod###koi_import"))
            {
                _progress = new Limber.BodyRig.OneClickRig.Progress();
                _ = KoiProps.Import(_ticked.ToList(), Math.Clamp(_config.KoiPropsMaxTexture, 64, 4096), _progress);
            }
        }
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Converts the ticked items (ones converted before are made again) and saves the mod. Props with bones are made as they rest (still); "
                + "compressed meshes and crunched textures aren't read yet (those parts come out plain).");
        DrawProgress();
    }

    private void DrawProgress()
    {
        if(_progress == null)
            return;
        var lines = _progress.Lines;
        using var child = ImRaii.Child("###koi_progress", new Vector2(-1, 140 * ImGuiHelpers.GlobalScale), true);
        if(!child.Success)
            return;
        foreach(var l in lines)
            ImGui.TextWrapped(l);
        if(!_progress.Done)
            ImGui.TextDisabled("Working...");
    }
}
