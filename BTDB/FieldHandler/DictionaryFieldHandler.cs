using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using BTDB.IL;
using BTDB.StreamLayer;

namespace BTDB.FieldHandler
{
    public class DictionaryFieldHandler : IFieldHandler
    {
        readonly IFieldHandlerFactory _fieldHandlerFactory;
        readonly ITypeConvertorGenerator _typeConvertorGenerator;
        readonly byte[] _configuration;
        readonly IFieldHandler _keysHandler;
        readonly IFieldHandler _valuesHandler;
        Type _type;

        public DictionaryFieldHandler(IFieldHandlerFactory fieldHandlerFactory, ITypeConvertorGenerator typeConvertorGenerator, Type type)
        {
            _fieldHandlerFactory = fieldHandlerFactory;
            _typeConvertorGenerator = typeConvertorGenerator;
            _type = type;
            _keysHandler = _fieldHandlerFactory.CreateFromType(type.GetGenericArguments()[0]);
            _valuesHandler = _fieldHandlerFactory.CreateFromType(type.GetGenericArguments()[1]);
            var writer = new ByteArrayWriter();
            writer.WriteFieldHandler(_keysHandler);
            writer.WriteFieldHandler(_valuesHandler);
            _configuration = writer.Data;
        }

        public DictionaryFieldHandler(IFieldHandlerFactory fieldHandlerFactory, ITypeConvertorGenerator typeConvertorGenerator, byte[] configuration)
        {
            _fieldHandlerFactory = fieldHandlerFactory;
            _typeConvertorGenerator = typeConvertorGenerator;
            _configuration = configuration;
            var reader = new ByteArrayReader(configuration);
            _keysHandler = _fieldHandlerFactory.CreateFromReader(reader);
            _valuesHandler = _fieldHandlerFactory.CreateFromReader(reader);
        }

        DictionaryFieldHandler(IFieldHandlerFactory fieldHandlerFactory, ITypeConvertorGenerator typeConvertorGenerator, Type type, IFieldHandler keySpecialized, IFieldHandler valueSpecialized)
        {
            _fieldHandlerFactory = fieldHandlerFactory;
            _typeConvertorGenerator = typeConvertorGenerator;
            _type = type;
            _keysHandler = keySpecialized;
            _valuesHandler = valueSpecialized;
        }

        public static string HandlerName
        {
            get { return "Dictionary"; }
        }

        public string Name
        {
            get { return HandlerName; }
        }

        public byte[] Configuration
        {
            get { return _configuration; }
        }

        public static bool IsCompatibleWith(Type type)
        {
            if (!type.IsGenericType) return false;
            return type.GetGenericTypeDefinition() == typeof(IDictionary<,>);
        }

        bool IFieldHandler.IsCompatibleWith(Type type)
        {
            return IsCompatibleWith(type);
        }

        public Type HandledType()
        {
            return _type ?? (_type = typeof(IDictionary<,>).MakeGenericType(_keysHandler.HandledType(), _valuesHandler.HandledType()));
        }

        public bool NeedsCtx()
        {
            return true;
        }

        public void Load(ILGenerator ilGenerator, Action<ILGenerator> pushReaderOrCtx)
        {
            var localCount = ilGenerator.DeclareLocal(typeof(uint));
            var localResultOfObject = ilGenerator.DeclareLocal(typeof(object));
            var localResult = ilGenerator.DeclareLocal(HandledType());
            var loadSkipped = ilGenerator.DefineLabel();
            var finish = ilGenerator.DefineLabel();
            var readfinish = ilGenerator.DefineLabel();
            var next = ilGenerator.DefineLabel();
            var genericArguments = _type.GetGenericArguments();
            object fake;
            ilGenerator
                .Do(pushReaderOrCtx)
                .Ldloca(localResultOfObject)
                .Callvirt(() => ((IReaderCtx)null).ReadObject(out fake))
                .Brfalse(loadSkipped)
                .Do(Extensions.PushReaderFromCtx(pushReaderOrCtx))
                .Callvirt(() => ((AbstractBufferedReader)null).ReadVUInt32())
                .Stloc(localCount)
                .Ldloc(localCount)
                .Newobj(typeof(Dictionary<,>).MakeGenericType(genericArguments).GetConstructor(new[] { typeof(int) }))
                .Stloc(localResult)
                .Do(pushReaderOrCtx)
                .Ldloc(localResult)
                .Castclass(typeof(object))
                .Callvirt(() => ((IReaderCtx)null).RegisterObject(null))
                .Mark(next)
                .Ldloc(localCount)
                .Brfalse(readfinish)
                .Ldloc(localCount)
                .LdcI4(1)
                .Sub()
                .ConvU4()
                .Stloc(localCount)
                .Ldloc(localResult)
                .GenerateLoad(_keysHandler, genericArguments[0], pushReaderOrCtx, _typeConvertorGenerator)
                .GenerateLoad(_valuesHandler, genericArguments[1], pushReaderOrCtx, _typeConvertorGenerator)
                .Callvirt(_type.GetMethod("Add"))
                .Br(next)
                .Mark(readfinish)
                .Do(pushReaderOrCtx)
                .Callvirt(() => ((IReaderCtx)null).ReadObjectDone())
                .Br(finish)
                .Mark(loadSkipped)
                .Ldloc(localResultOfObject)
                .Isinst(_type)
                .Stloc(localResult)
                .Mark(finish)
                .Ldloc(localResult);
        }

        public void SkipLoad(ILGenerator ilGenerator, Action<ILGenerator> pushReaderOrCtx)
        {
            var localCount = ilGenerator.DeclareLocal(typeof(uint));
            var finish = ilGenerator.DefineLabel();
            var next = ilGenerator.DefineLabel();
            ilGenerator
                .Do(pushReaderOrCtx)
                .Callvirt(() => ((IReaderCtx) null).SkipObject())
                .Brfalse(finish)
                .Do(Extensions.PushReaderFromCtx(pushReaderOrCtx))
                .Callvirt(() => ((AbstractBufferedReader) null).ReadVUInt32())
                .Stloc(localCount)
                .Mark(next)
                .Ldloc(localCount)
                .Brfalse(finish)
                .Ldloc(localCount)
                .LdcI4(1)
                .Sub()
                .ConvU4()
                .Stloc(localCount)
                .GenerateSkip(_keysHandler, pushReaderOrCtx)
                .GenerateSkip(_valuesHandler, pushReaderOrCtx)
                .Br(next)
                .Mark(finish);
        }

        public void Save(ILGenerator ilGenerator, Action<ILGenerator> pushWriterOrCtx, Action<ILGenerator> pushValue)
        {
            var realfinish = ilGenerator.DefineLabel();
            var finish = ilGenerator.DefineLabel();
            var next = ilGenerator.DefineLabel();
            var localValue = ilGenerator.DeclareLocal(_type);
            var typeAsICollection = _type.GetInterface("ICollection`1");
            var typeAsIEnumerable = _type.GetInterface("IEnumerable`1");
            var getEnumeratorMethod = typeAsIEnumerable.GetMethod("GetEnumerator");
            var typeAsIEnumerator = getEnumeratorMethod.ReturnType;
            var typeKeyValuePair = typeAsICollection.GetGenericArguments()[0];
            var localEnumerator = ilGenerator.DeclareLocal(typeAsIEnumerator);
            var localPair = ilGenerator.DeclareLocal(typeKeyValuePair);
            ilGenerator
                .Do(pushValue)
                .Stloc(localValue)
                .Do(pushWriterOrCtx)
                .Ldloc(localValue)
                .Castclass(typeof(object))
                .Callvirt(() => ((IWriterCtx)null).WriteObject(null))
                .Brfalse(realfinish)
                .Do(Extensions.PushWriterFromCtx(pushWriterOrCtx))
                .Ldloc(localValue)
                .Callvirt(typeAsICollection.GetProperty("Count").GetGetMethod())
                .ConvU4()
                .Callvirt(() => ((AbstractBufferedWriter)null).WriteVUInt32(0))
                .Ldloc(localValue)
                .Callvirt(getEnumeratorMethod)
                .Stloc(localEnumerator)
                .Try()
                .Mark(next)
                .Ldloc(localEnumerator)
                .Callvirt(() => ((IEnumerator)null).MoveNext())
                .Brfalse(finish)
                .Ldloc(localEnumerator)
                .Callvirt(typeAsIEnumerator.GetProperty("Current").GetGetMethod())
                .Stloc(localPair);
            _keysHandler.Save(ilGenerator, Extensions.PushWriterOrCtxAsNeeded(pushWriterOrCtx, _keysHandler.NeedsCtx()), il => il
                .Ldloca(localPair)
                .Call(typeKeyValuePair.GetProperty("Key").GetGetMethod())
                .Do(_typeConvertorGenerator.GenerateConversion(_type.GetGenericArguments()[0], _keysHandler.HandledType())));
            _valuesHandler.Save(ilGenerator, Extensions.PushWriterOrCtxAsNeeded(pushWriterOrCtx, _valuesHandler.NeedsCtx()), il => il
                .Ldloca(localPair)
                .Call(typeKeyValuePair.GetProperty("Value").GetGetMethod())
                .Do(_typeConvertorGenerator.GenerateConversion(_type.GetGenericArguments()[1], _valuesHandler.HandledType())));
            ilGenerator
                .Br(next)
                .Mark(finish)
                .Finally()
                .Ldloc(localEnumerator)
                .Callvirt(() => ((IDisposable)null).Dispose())
                .EndTry()
                .Mark(realfinish);
        }

        public IFieldHandler SpecializeLoadForType(Type type)
        {
            if (_type == type) return this;
            if (!IsCompatibleWith(type))
            {
                Debug.Fail("strange");
                return this;
            }
            var wantedKeyType = type.GetGenericArguments()[0];
            var wantedValueType = type.GetGenericArguments()[1];
            var keySpecialized = _keysHandler.SpecializeLoadForType(wantedKeyType);
            if (_typeConvertorGenerator.GenerateConversion(keySpecialized.HandledType(), wantedKeyType) == null)
            {
                Debug.Fail("even more strange key");
                return this;
            }
            var valueSpecialized = _valuesHandler.SpecializeLoadForType(wantedValueType);
            if (_typeConvertorGenerator.GenerateConversion(valueSpecialized.HandledType(), wantedValueType) == null)
            {
                Debug.Fail("even more strange value");
                return this;
            }
            return new DictionaryFieldHandler(_fieldHandlerFactory, _typeConvertorGenerator, type, keySpecialized,valueSpecialized);
        }

        public IFieldHandler SpecializeSaveForType(Type type)
        {
            if (_type == type) return this;
            if (!IsCompatibleWith(type))
            {
                Debug.Fail("strange");
                return this;
            }
            var wantedKeyType = type.GetGenericArguments()[0];
            var wantedValueType = type.GetGenericArguments()[1];
            var keySpecialized = _keysHandler.SpecializeSaveForType(wantedKeyType);
            if (_typeConvertorGenerator.GenerateConversion(wantedKeyType, keySpecialized.HandledType()) == null)
            {
                Debug.Fail("even more strange key");
                return this;
            }
            var valueSpecialized = _valuesHandler.SpecializeSaveForType(wantedValueType);
            if (_typeConvertorGenerator.GenerateConversion(wantedValueType, valueSpecialized.HandledType()) == null)
            {
                Debug.Fail("even more strange value");
                return this;
            }
            return new DictionaryFieldHandler(_fieldHandlerFactory, _typeConvertorGenerator, type, keySpecialized, valueSpecialized);
        }
    }
}