namespace RpcClientSdk.Mar07
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using NsAnyLR;

    public readonly struct AssociationMapping
    {
        public readonly Dictionary<Type, IEnumerable<Type>> Dict;

        public AssociationMapping(Dictionary<Type, IEnumerable<Type>> dict)
            => this.Dict = dict;

        public static implicit operator AssociationMapping(Dictionary<Type, IEnumerable<Type>> dict)
            => new AssociationMapping(dict);

        public static implicit operator Dictionary<Type, IEnumerable<Type>>(AssociationMapping mapping)
            => mapping.Dict;
    }

    public readonly struct TypeHexMapping
    {
        public readonly SortedDictionary<uint, Type> Dict;

        public TypeHexMapping(SortedDictionary<uint, Type> dict)
            => this.Dict = dict;

        public static implicit operator TypeHexMapping(SortedDictionary<uint, Type> dict)
            => new TypeHexMapping(dict);

        public static implicit operator SortedDictionary<uint, Type>(TypeHexMapping mapping)
            => mapping.Dict;
    }

    public interface IApiTypeInit
    {
        public AssociationMapping InitAssociation();

        public TypeHexMapping InitTypeHex();
    }

    public interface IApiTypeBind
    {
        IEnumerable<Type> FindBinding(Type apiType);

        Option<Type> FindType(uint typeHex);

        IEnumerable<(uint, Type)> GetTypeHexEntries();

        IEnumerable<(Type, IEnumerable<Type>)> GetTypeBindEntries();
    }

    public sealed class ApiInitException : Exception
    {
        private readonly (Type, Option<Type>) violation_;

        public ApiInitException((Type, Option<Type>) violation)
            => this.violation_ = violation;

        public (Type, Option<Type>) Violation
            => this.violation_;
    }

    public sealed class ApiTypeAssocCache<T> : IApiTypeBind
        where T : IApiTypeInit, new()
    {
        private readonly AssociationMapping aMap_;

        private readonly TypeHexMapping hMap_;

        public ApiTypeAssocCache()
        {
            var x = new T();
            var amap = x.InitAssociation();
            var hmap = x.InitTypeHex();
            if (amap.Dict is null || hmap.Dict is null)
                throw new ArgumentException();
            foreach (var kv in amap.Dict)
            {
                var apiType = kv.Key;
                if (!apiType.IsAbstract && !apiType.IsInterface)
                    throw new ApiInitException((apiType, new Option<Type>()));

                var types = kv.Value;
                var violations = types.Where(t => !apiType.IsAssignableFrom(t));
                if (violations.Any() && violations.First() is Type t)
                    throw new ApiInitException((apiType, Option.Some(t)));
            }
            this.aMap_ = amap;
            this.hMap_ = hmap;
        }

        public IEnumerable<Type> FindBinding(Type apiType)
        {
            if (this.aMap_.Dict.TryGetValue(apiType, out var types))
                return types;
            else
                return Enumerable.Empty<Type>();
        }

        public Option<Type> FindType(uint typeHex)
        {
            if (this.hMap_.Dict.TryGetValue(typeHex, out var type))
                return Option.Some(type);
            else
                return Option.None();
        }

        IEnumerable<(uint, Type)> IApiTypeBind.GetTypeHexEntries()
            => this.hMap_.Dict.Select(kv => (kv.Key, kv.Value));

        IEnumerable<(Type, IEnumerable<Type>)> IApiTypeBind.GetTypeBindEntries()
            => this.aMap_.Dict.Select(kv => (kv.Key, kv.Value));
    }
}