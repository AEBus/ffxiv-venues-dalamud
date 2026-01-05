using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIVVenues.Dalamud.Utils;

internal sealed class TypeMap<T> where T : class
{
    private readonly Dictionary<string, Type> _typeMap = new();
    private readonly IServiceProvider _serviceProvider;

    public TypeMap(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public string[] Keys => _typeMap.Keys.ToArray();

    public TypeMap<T> Add<TConcrete>(string key) where TConcrete : T
    {
        _typeMap.Add(key, typeof(TConcrete));
        return this;
    }

    public TypeMap<T> Add(string key, Type type)
    {
        if (!type.IsAssignableTo(typeof(T)))
        {
            throw new ArgumentException($"Type {type.Name} is not of type {typeof(T).Name}");
        }

        _typeMap.Add(key, type);
        return this;
    }

    public bool ContainsKey(string key) => _typeMap.ContainsKey(key);

    public T? Activate(string key, IServiceProvider? serviceProvider = null)
    {
        serviceProvider ??= _serviceProvider;
        if (!_typeMap.TryGetValue(key, out var type))
        {
            return null;
        }

        return ActivatorUtilities.CreateInstance(serviceProvider, type) as T;
    }
}
