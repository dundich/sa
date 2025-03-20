//namespace Sa.Host.MessageTypeResolver;

//using System.Collections.Concurrent;
//using System.Text.RegularExpressions;

///// <summary>
///// <see cref="IMessageTypeResolver"/> that uses the <see cref="Type.AssemblyQualifiedName"/> for the message type string passed in the message header.
///// </summary>
//public class AssemblyQualifiedNameMessageTypeResolver : IMessageTypeResolver
//{
//    private static readonly Regex s_RedundantAssemblyTokens = new(@"\, (Version|Culture|PublicKeyToken)\=([\w\d.]+)", RegexOptions.None, TimeSpan.FromSeconds(2));

//    /// <summary>
//    /// Determines whether to emit the Version, Culture and PublicKeyToken along with the Assembly name (for strong assembly naming).
//    /// </summary>
//    public bool EmitAssemblyStrongName { get; set; } = false;

//    private readonly ConcurrentDictionary<Type, string> _toNameCache = [];
//    private readonly ConcurrentDictionary<string, Type> _toTypeCache = [];



//    private string ToNameInternal(Type messageType)
//    {
//        string assemblyQualifiedName = messageType?.AssemblyQualifiedName ?? throw new ArgumentNullException(nameof(messageType));

//        if (EmitAssemblyStrongName)
//        {
//            return assemblyQualifiedName;
//        }

//        var reducedName = s_RedundantAssemblyTokens.Replace(assemblyQualifiedName, string.Empty);

//        return reducedName;
//    }

//    private static Type ToTypeInternal(string name)
//        => Type.GetType(name ?? throw new ArgumentNullException(nameof(name))) ?? throw new ArgumentException(null, nameof(name));

//    public string ToName(Type messageType)
//    {
//        if (!_toNameCache.TryGetValue(messageType, out _))
//        {
//            string typeName = ToNameInternal(messageType);

//            if (_toNameCache.TryAdd(messageType, typeName))
//            {
//                _toTypeCache.TryAdd(typeName, messageType);
//            }
//        }

//        return _toNameCache.GetValueOrDefault(messageType) ?? throw new ArgumentException(null, nameof(messageType));
//    }

//    public Type ToType(string name)
//    {
//        if (!_toTypeCache.TryGetValue(name, out _))
//        {
//            Type? messageType = ToTypeInternal(name);

//            if (_toTypeCache.TryAdd(name, messageType))
//            {
//                _toNameCache.TryAdd(messageType, name);
//            }

//            return messageType;
//        }

//        return _toTypeCache.GetValueOrDefault(name) ?? throw new ArgumentException(null, nameof(name));
//    }
//}