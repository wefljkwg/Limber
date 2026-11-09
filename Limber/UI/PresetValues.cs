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

    public PresetValues Add<T>(string name, Func<T> get, Action<T> set)
    {
        _items.Add((name, typeof(T), () => get(), v => set((T)v!)));
        return this;
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
