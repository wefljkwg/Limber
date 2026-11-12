using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Limber.UI;

/// <summary>
/// Single settings (a checkbox, a slider, a list) gathered for a page's presets: each saved under a name and put back
/// through its setter. For pages whose settings aren't one object that can be saved whole.
/// </summary>
public sealed class PresetValues
{
    private readonly List<(string Name, Type Type, Func<object?> Get, Action<object?> Set)> _items = new();
    // Defaults given here (for values that aren't in the settings, so a fresh settings object can't tell them).
    private readonly Dictionary<string, object?> _defaults = new();

    public PresetValues Add<T>(string name, Func<T> get, Action<T> set)
    {
        _items.Add((name, typeof(T), () => get(), v => set((T)v!)));
        return this;
    }

    /// <summary>The default of the value just added (Reset to default puts this back).</summary>
    public PresetValues Default<T>(T value)
    {
        if(_items.Count > 0)
            _defaults[_items[^1].Name] = value;
        return this;
    }

    /// <summary>
    /// Every value back to its default: the one given with <see cref="Default{T}"/>, else the same value in
    /// <paramref name="defaults"/> (these values on fresh settings). A copy is put back, never the default object itself.
    /// </summary>
    public void Reset(PresetValues? defaults, JsonSerializer json)
    {
        foreach(var (name, type, get, set) in _items)
        {
            object? value;
            if(_defaults.TryGetValue(name, out var given))
                value = given;
            else if(defaults?._items.Find(i => i.Name == name) is { Name: not null } d)
                value = d.Get();
            else
                continue;
            try
            {
                var token = value == null ? JValue.CreateNull() : JToken.FromObject(value, json);
                set(token.Type == JTokenType.Null ? null : token.ToObject(type, json));
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, $"Limber: {name} couldn't be put back to its default");
            }
        }
    }

    public JToken Save(JsonSerializer json)
    {
        var o = new JObject();
        foreach(var (name, _, get, _) in _items)
        {
            var value = get();
            o[name] = value == null ? JValue.CreateNull() : JToken.FromObject(value, json);
        }
        return o;
    }

    public void Load(JToken token, JsonSerializer json)
    {
        if(token is not JObject o)
            return;
        foreach(var (name, type, _, set) in _items)
        {
            if(o[name] is not { } value || value.Type == JTokenType.Null)
                continue;
            try
            {
                set(value.ToObject(type, json));
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, $"Limber: preset value {name} couldn't be put back");
            }
        }
    }
}
