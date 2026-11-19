using System;
using System.Collections.Generic;
using System.Reflection;
using FishNet.Object.Synchronizing;
using FishNet.Serializing;
using UnityEngine;

namespace BIG.Unity.Networking
{
    /// <summary>
    /// On a field of a <see cref="NetworkBaseBehaviour"/>: the field survives a host migration (the object needs
    /// <see cref="NetworkMigratable"/>). Works on plain fields and on <c>SyncVar&lt;T&gt;</c>. Types: bool, int, uint, long,
    /// float, double, string, enums, Vector2/3/4, Quaternion, Color, Color32 — anything else: override
    /// OnWriteMigration / OnReadMigration.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class MigrateAttribute : Attribute { }

    /// <summary> Writes / reads the [Migrate] fields of a behaviour. Reflection once per type, then cached. </summary>
    internal static class MigrateFields
    {
        private sealed class Field
        {
            public FieldInfo Info;
            public PropertyInfo SyncVarValue; // Set when the field is a SyncVar<T>: read / write .Value.
            public Type ValueType;
            public Action<Writer, object> Write;
            public Func<Reader, object> Read;
        }

        private static readonly Dictionary<Type, Field[]> Cache = new Dictionary<Type, Field[]>();

        // AOT-safe: a fixed table, no generic serializer resolved at runtime.
        private static readonly Dictionary<Type, (Action<Writer, object> write, Func<Reader, object> read)> Serializers =
            new Dictionary<Type, (Action<Writer, object>, Func<Reader, object>)>
            {
                [typeof(bool)] = ((w, v) => w.WriteBoolean((bool)v), r => r.ReadBoolean()),
                [typeof(int)] = ((w, v) => w.WriteInt32((int)v), r => r.ReadInt32()),
                [typeof(uint)] = ((w, v) => w.WriteUInt32((uint)v), r => r.ReadUInt32()),
                [typeof(long)] = ((w, v) => w.WriteInt64((long)v), r => r.ReadInt64()),
                [typeof(float)] = ((w, v) => w.WriteSingle((float)v), r => r.ReadSingle()),
                [typeof(double)] = ((w, v) => w.WriteDouble((double)v), r => r.ReadDouble()),
                [typeof(string)] = ((w, v) => w.WriteString((string)v ?? ""), r => r.ReadStringAllocated()),
                [typeof(Vector2)] = ((w, v) => w.WriteVector2((Vector2)v), r => r.ReadVector2()),
                [typeof(Vector3)] = ((w, v) => w.WriteVector3((Vector3)v), r => r.ReadVector3()),
                [typeof(Vector4)] = ((w, v) => w.WriteVector4((Vector4)v), r => r.ReadVector4()),
                [typeof(Quaternion)] = ((w, v) => w.WriteQuaternionUnpacked((Quaternion)v), r => r.ReadQuaternionUnpacked()),
                [typeof(Color)] = ((w, v) => w.WriteColorUnpacked((Color)v), r => r.ReadColorUnpacked()),
                [typeof(Color32)] = ((w, v) => w.WriteColor32((Color32)v), r => r.ReadColor32()),
            };

        public static void Write(NetworkBaseBehaviour behaviour, Writer writer)
        {
            foreach (var field in FieldsOf(behaviour.GetType()))
                field.Write(writer, GetValue(field, behaviour));
        }

        public static void Read(NetworkBaseBehaviour behaviour, Reader reader)
        {
            foreach (var field in FieldsOf(behaviour.GetType()))
                SetValue(field, behaviour, field.Read(reader));
        }

        private static object GetValue(Field field, object owner)
        {
            object value = field.Info.GetValue(owner);
            if (field.SyncVarValue != null) value = field.SyncVarValue.GetValue(value);
            return field.ValueType.IsEnum ? Convert.ToInt32(value) : value;
        }

        private static void SetValue(Field field, object owner, object value)
        {
            if (field.ValueType.IsEnum) value = Enum.ToObject(field.ValueType, (int)value);
            if (field.SyncVarValue != null)
                field.SyncVarValue.SetValue(field.Info.GetValue(owner), value); // SyncVar → synced to everyone as usual.
            else
                field.Info.SetValue(owner, value);
        }

        private static Field[] FieldsOf(Type type)
        {
            if (Cache.TryGetValue(type, out var cached)) return cached;

            var fields = new List<Field>();
            // Base classes first, declaration order within each — the same on every machine running this build.
            var chain = new List<Type>();
            for (var t = type; t != null && t != typeof(NetworkBaseBehaviour); t = t.BaseType) chain.Insert(0, t);

            foreach (var t in chain)
            {
                foreach (var info in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (info.GetCustomAttribute<MigrateAttribute>() == null) continue;

                    var field = new Field { Info = info, ValueType = info.FieldType };
                    if (info.FieldType.IsGenericType && info.FieldType.GetGenericTypeDefinition() == typeof(SyncVar<>))
                    {
                        field.ValueType = info.FieldType.GetGenericArguments()[0];
                        field.SyncVarValue = info.FieldType.GetProperty("Value");
                    }

                    var key = field.ValueType.IsEnum ? typeof(int) : field.ValueType;
                    if (!Serializers.TryGetValue(key, out var serializer))
                        throw new NotSupportedException($"[Network] [Migrate] {type.Name}.{info.Name}: type {field.ValueType.Name} isn't supported " +
                                                        "— write it in OnWriteMigration / OnReadMigration instead.");
                    field.Write = serializer.write;
                    field.Read = serializer.read;
                    fields.Add(field);
                }
            }

            return Cache[type] = fields.ToArray();
        }
    }
}
