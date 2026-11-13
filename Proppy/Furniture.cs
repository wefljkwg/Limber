using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Proppy;

/// <summary>Every housing item the game has (indoor furnishings and outdoor yard items), with its layout file.</summary>
public sealed class Furniture
{
    public sealed record Item(string Name, uint ModelKey, bool Indoors, uint Icon)
    {
        /// <summary>The item's shared-group layout (its models, collision and lights), as the housing system places it.</summary>
        public string Path => Indoors
            ? $"bgcommon/hou/indoor/general/{ModelKey:0000}/asset/fun_b0_m{ModelKey:0000}.sgb"
            : $"bgcommon/hou/outdoor/general/{ModelKey:0000}/asset/gar_b0_m{ModelKey:0000}.sgb";
    }

    public IReadOnlyList<Item> All { get; }

    public Furniture(IDataManager data)
    {
        var list = new List<Item>();
        var indoor = data.GetExcelSheet<HousingFurniture>();
        if(indoor != null)
            foreach(var row in indoor)
            {
                var item = row.Item.ValueNullable;
                if(row.ModelKey == 0 || item is null)
                    continue;
                string name = item.Value.Name.ToString();
                if(name.Length > 0)
                    list.Add(new Item(name, row.ModelKey, true, item.Value.Icon));
            }
        var outdoor = data.GetExcelSheet<HousingYardObject>();
        if(outdoor != null)
            foreach(var row in outdoor)
            {
                var item = row.Item.ValueNullable;
                if(row.ModelKey == 0 || item is null)
                    continue;
                string name = item.Value.Name.ToString();
                if(name.Length > 0)
                    list.Add(new Item(name, row.ModelKey, false, item.Value.Icon));
            }
        // (one per model: several items share one)
        All = list.GroupBy(i => (i.ModelKey, i.Indoors)).Select(g => g.First())
            .Where(i => data.FileExists(i.Path))
            .OrderBy(i => i.Name).ToList();
    }
}
