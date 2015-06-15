using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace FluentMapping
{
    public sealed class TypeMappingSpec<TTarget, TSource>
    {
        public IEnumerable<SourceValue<TSource>> SourceValues { get; private set; }
        public IEnumerable<ITargetValue<TTarget>> TargetValues { get; private set; }
        public IEnumerable<Expression> CustomMappings { get; private set; }
        public Func<TTarget> ConstructorFunc { get; private set; }

        public TypeMappingSpec() : this(GetDefaultTargetValues(), GetDefaultSourceValues(), new Expression[0], null)
        {
        }

        public TypeMappingSpec(
            ITargetValue<TTarget>[] targetValues, 
            SourceValue<TSource>[] sourceValues, 
            Expression[] customMappings,
            Func<TTarget> constructorFunc
            )
        {
            TargetValues = targetValues;
            SourceValues = sourceValues;
            CustomMappings = customMappings ?? Enumerable.Empty<Expression>();
            ConstructorFunc = constructorFunc;
        }

        public IMapper<TTarget, TSource> Create()
        {
            ValidateMapping();

            var constructor = GetConstructor();

            var targetParam = Expression.Parameter(typeof (TTarget));
            var sourceParam = Expression.Parameter(typeof (TSource));

            var setterActions = TargetValues.OrderBy(x => x.PropertyName)
                
                .Zip(SourceValues.OrderBy(x => x.PropertyName), (tgt, src) => tgt.CreateSetter(src.CreateGetter()))
                .Concat(CustomMappings)
                .Select(x => EnsureReturnsTarget(x))
                .ToArray()
                ;
            
            var accumulatedLambda = Expression.Invoke(setterActions.First(), targetParam, sourceParam);

            foreach (var setterExpr in setterActions.Skip(1))
            {
                accumulatedLambda = Expression.Invoke(setterExpr, accumulatedLambda, sourceParam);
            }

            var mapperAction = Expression.Lambda<Func<TTarget, TSource, TTarget>>(accumulatedLambda, targetParam, sourceParam);

            return new SimpleMapper<TTarget, TSource>(constructor, mapperAction.Compile());
        }

        private Func<TTarget> GetConstructor()
        {
            if(ConstructorFunc != null)
                return ConstructorFunc;

            var ctorInfo = typeof (TTarget).GetConstructor(new Type [0]);

            return Expression.Lambda<Func<TTarget>>(
                Expression.New(ctorInfo)
                ).Compile();
        }

        public ContextualTypeMappingSpec<TTarget, TSource, TContext> UsingContext<TContext>()
        {
            return new ContextualTypeMappingSpec<TTarget, TSource, TContext>(this);
        }

        public SetterSpec<TTarget, TSource, TProperty> ThatSets<TProperty>(
            Expression<Func<TTarget, TProperty>> propertyExpression)
        {
            return new SetterSpec<TTarget, TSource, TProperty>(this, propertyExpression);
        }

        private void ValidateMapping()
        {
            var targetNames = TargetValues.Select(x => x.PropertyName);
            var sourceNames = SourceValues.Select(x => x.PropertyName);

            var unmatchedTargets = targetNames.Except(sourceNames);

            foreach (var targetProperty in unmatchedTargets)
            {
                ThrowUnmatchedTarget(TargetValues.First(x => x.PropertyName == targetProperty));
            }

            var unmatchedSources = sourceNames.Except(targetNames);

            foreach (var sourceProperty in unmatchedSources)
            {
                ThrowUnmatchedSource(SourceValues.First(x => x.PropertyName == sourceProperty));
            }

            var mismatchedTypes = SourceValues.OrderBy(x => x.PropertyName)
                .Zip(TargetValues.OrderBy(x => x.PropertyName), (src, tgt) => new
                {
                    src,
                    tgt
                })
                .Where(x => x.src.ValueType != x.tgt.ValueType);

            foreach (var mismatch in mismatchedTypes)
            {
                var msg = string.Format(
                    "Cannot map [{0}] from [{1}].",
                    mismatch.tgt.Description,
                    mismatch.src.Description
                    );
                throw new Exception(msg);
            }

        }

        private static void ThrowUnmatchedTarget(ITargetValue<TTarget> value)
        {
            var message = string.Format("Target {0} is unmatched.",
                value.Description);
            throw new Exception(message);
        }

        private static void ThrowUnmatchedSource(SourceValue<TSource> value)
        {
            var message = string.Format("Source {0} is unmatched.",
                value.Description);
            throw new Exception(message);
        }

        private static string GetDescription<T>(IValue<T> value)
        {
            return string.Format(
                "{0} {1}.{2}",
                value.ValueType.Name,
                typeof(T).Name,
                value.PropertyName
                );
        }

        private static SourceValue<TSource>[] GetDefaultSourceValues()
        {
            return GetProperties(typeof(TSource))
                .Select(x => new SourceValue<TSource>(x))
                .ToArray();
        }

        private static ITargetValue<TTarget>[] GetDefaultTargetValues()
        {
            // ReSharper disable once CoVariantArrayConversion
            return typeof (TTarget).GetProperties()
                .Where(x => x.CanWrite && x.GetSetMethod() != null && x.GetSetMethod().IsPublic)
                .Select(x => new TargetValue<TTarget>(x))
                .ToArray();
        }

        private static LambdaExpression EnsureReturnsTarget(Expression e)
        {
            var lambda = e as LambdaExpression;

            if (lambda.ReturnType == typeof (TTarget))
                return lambda;

            var targetParam = lambda.Parameters[0];
            var sourceParam = lambda.Parameters[1];

            var block = Expression.Block(lambda.Body, targetParam);

            return Expression.Lambda(block, targetParam, sourceParam);
        }

        private static PropertyInfo[] GetProperties(Type type)
        {
            if (!type.IsInterface)
                return type.GetProperties();

            return (new Type[] { type })
                   .Concat(type.GetInterfaces())
                   .SelectMany(i => i.GetProperties()).ToArray();
        }
    }
}