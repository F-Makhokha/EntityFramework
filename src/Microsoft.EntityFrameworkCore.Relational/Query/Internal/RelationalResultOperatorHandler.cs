// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Expressions;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.ResultOperators;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class RelationalResultOperatorHandler : IRelationalResultOperatorHandler
    {
        private sealed class HandlerContext
        {
            private readonly IResultOperatorHandler _resultOperatorHandler;
            private readonly ISqlTranslatingExpressionVisitorFactory _sqlTranslatingExpressionVisitorFactory;

            public HandlerContext(
                IResultOperatorHandler resultOperatorHandler,
                IModel model,
                ISqlTranslatingExpressionVisitorFactory sqlTranslatingExpressionVisitorFactory,
                ISelectExpressionFactory selectExpressionFactory,
                RelationalQueryModelVisitor queryModelVisitor,
                ResultOperatorBase resultOperator,
                QueryModel queryModel,
                SelectExpression selectExpression)
            {
                _resultOperatorHandler = resultOperatorHandler;
                _sqlTranslatingExpressionVisitorFactory = sqlTranslatingExpressionVisitorFactory;

                Model = model;
                SelectExpressionFactory = selectExpressionFactory;
                QueryModelVisitor = queryModelVisitor;
                ResultOperator = resultOperator;
                QueryModel = queryModel;
                SelectExpression = selectExpression;
            }

            public IModel Model { get; }
            public ISelectExpressionFactory SelectExpressionFactory { get; }
            public ResultOperatorBase ResultOperator { get; }
            public SelectExpression SelectExpression { get; }
            public QueryModel QueryModel { get; }
            public RelationalQueryModelVisitor QueryModelVisitor { get; }
            public Expression EvalOnServer => QueryModelVisitor.Expression;

            public Expression EvalOnClient(bool requiresClientResultOperator = true)
            {
                QueryModelVisitor.RequiresClientResultOperator = requiresClientResultOperator;

                return _resultOperatorHandler
                    .HandleResultOperator(QueryModelVisitor, ResultOperator, QueryModel);
            }

            public SqlTranslatingExpressionVisitor CreateSqlTranslatingVisitor(bool bindParentQueries = false)
                => _sqlTranslatingExpressionVisitorFactory
                    .Create(
                        QueryModelVisitor,
                        SelectExpression,
                        bindParentQueries: bindParentQueries);
        }

        private static readonly Dictionary<Type, Func<HandlerContext, Expression>>
            _resultHandlers = new Dictionary<Type, Func<HandlerContext, Expression>>
            {
                { typeof(AllResultOperator), HandleAll },
                { typeof(AnyResultOperator), HandleAny },
                { typeof(AverageResultOperator), HandleAverage },
                { typeof(CastResultOperator), HandleCast },
                { typeof(ContainsResultOperator), HandleContains },
                { typeof(CountResultOperator), HandleCount },
                { typeof(LongCountResultOperator), HandleLongCount },
                { typeof(DefaultIfEmptyResultOperator), HandleDefaultIfEmpty },
                { typeof(DistinctResultOperator), HandleDistinct },
                { typeof(FirstResultOperator), HandleFirst },
                { typeof(GroupResultOperator), HandleGroup },
                { typeof(LastResultOperator), HandleLast },
                { typeof(MaxResultOperator), HandleMax },
                { typeof(MinResultOperator), HandleMin },
                { typeof(SingleResultOperator), HandleSingle },
                { typeof(SkipResultOperator), HandleSkip },
                { typeof(SumResultOperator), HandleSum },
                { typeof(TakeResultOperator), HandleTake }
            };

        private readonly IModel _model;
        private readonly ISqlTranslatingExpressionVisitorFactory _sqlTranslatingExpressionVisitorFactory;
        private readonly ISelectExpressionFactory _selectExpressionFactory;
        private readonly IResultOperatorHandler _resultOperatorHandler;

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public RelationalResultOperatorHandler(
            [NotNull] IModel model,
            [NotNull] ISqlTranslatingExpressionVisitorFactory sqlTranslatingExpressionVisitorFactory,
            [NotNull] ISelectExpressionFactory selectExpressionFactory,
            [NotNull] IResultOperatorHandler resultOperatorHandler)
        {
            _model = model;
            _sqlTranslatingExpressionVisitorFactory = sqlTranslatingExpressionVisitorFactory;
            _selectExpressionFactory = selectExpressionFactory;
            _resultOperatorHandler = resultOperatorHandler;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual Expression HandleResultOperator(
            EntityQueryModelVisitor entityQueryModelVisitor,
            ResultOperatorBase resultOperator,
            QueryModel queryModel)
        {
            var relationalQueryModelVisitor
                = (RelationalQueryModelVisitor)entityQueryModelVisitor;

            var selectExpression
                = relationalQueryModelVisitor
                    .TryGetQuery(queryModel.MainFromClause);

            var handlerContext
                = new HandlerContext(
                    _resultOperatorHandler,
                    _model,
                    _sqlTranslatingExpressionVisitorFactory,
                    _selectExpressionFactory,
                    relationalQueryModelVisitor,
                    resultOperator,
                    queryModel,
                    selectExpression);

            Func<HandlerContext, Expression> resultHandler;
            if (relationalQueryModelVisitor.RequiresClientEval
                || relationalQueryModelVisitor.RequiresClientSelectMany
                || relationalQueryModelVisitor.RequiresClientJoin
                || relationalQueryModelVisitor.RequiresClientFilter
                || relationalQueryModelVisitor.RequiresClientOrderBy
                || relationalQueryModelVisitor.RequiresClientResultOperator
                || !_resultHandlers.TryGetValue(resultOperator.GetType(), out resultHandler)
                || selectExpression == null)
            {
                return handlerContext.EvalOnClient();
            }

            if (relationalQueryModelVisitor.RequiresClientSingleColumnResultOperator
                && !(resultOperator is SkipResultOperator
                    || resultOperator is TakeResultOperator
                    || resultOperator is FirstResultOperator
                    || resultOperator is SingleResultOperator
                    || resultOperator is CountResultOperator
                    || resultOperator is AllResultOperator
                    || resultOperator is AnyResultOperator
                    || resultOperator is GroupResultOperator))
            {
                return handlerContext.EvalOnClient();
            }

            return resultHandler(handlerContext);
        }

        private static Expression HandleAll(HandlerContext handlerContext)
        {
            var sqlTranslatingVisitor
                = handlerContext.CreateSqlTranslatingVisitor(bindParentQueries: true);

            var predicate
                = sqlTranslatingVisitor.Visit(
                    ((AllResultOperator)handlerContext.ResultOperator).Predicate);

            if (predicate != null)
            {
                var innerSelectExpression = handlerContext.SelectExpression.Clone();

                innerSelectExpression.ClearProjection();
                innerSelectExpression.AddToProjection(Expression.Constant(1));

                if (handlerContext.SelectExpression.Predicate != null)
                {
                    innerSelectExpression.Predicate
                        = Expression.AndAlso(
                            handlerContext.SelectExpression.Predicate,
                            Expression.Not(predicate));
                }
                else
                {
                    innerSelectExpression.Predicate = Expression.Not(predicate);
                }

                if (innerSelectExpression.Limit == null
                    && innerSelectExpression.Offset == null)
                {
                    innerSelectExpression.ClearOrderBy();
                }

                SetProjectionConditionalExpression(
                    handlerContext,
                    Expression.Condition(
                        Expression.Not(new ExistsExpression(innerSelectExpression)),
                        Expression.Constant(true),
                        Expression.Constant(false),
                        typeof(bool)));

                return TransformClientExpression<bool>(handlerContext);
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleAny(HandlerContext handlerContext)
        {
            var innerSelectExpression = handlerContext.SelectExpression.Clone();

            innerSelectExpression.ClearProjection();
            innerSelectExpression.AddToProjection(Expression.Constant(1));

            if (innerSelectExpression.Limit == null
                && innerSelectExpression.Offset == null)
            {
                innerSelectExpression.ClearOrderBy();
            }

            SetProjectionConditionalExpression(
                handlerContext,
                Expression.Condition(
                    new ExistsExpression(innerSelectExpression),
                    Expression.Constant(true),
                    Expression.Constant(false),
                    typeof(bool)));

            return TransformClientExpression<bool>(handlerContext);
        }

        private static Expression HandleAverage(HandlerContext handlerContext)
        {
            if (!handlerContext.QueryModelVisitor.RequiresClientProjection
                && handlerContext.SelectExpression.Projection.Count == 1)
            {
                var expression = handlerContext.SelectExpression.Projection.First();

                var inputType = expression.Type;
                var outputType = expression.Type;

                var nonNullableInputType = inputType.UnwrapNullableType();
                if (nonNullableInputType == typeof(int)
                    || nonNullableInputType == typeof(long))
                {
                    outputType = inputType.IsNullableType() ? typeof(double?) : typeof(double);
                }

                expression = new ExplicitCastExpression(expression, outputType);
                var averageExpression = new SqlFunctionExpression("AVG", expression.Type, new[] { expression });

                handlerContext.SelectExpression.SetProjectionExpression(averageExpression);

                return (Expression)_transformClientExpressionMethodInfo
                    .MakeGenericMethod(averageExpression.Type)
                    .Invoke(null, new object[] { handlerContext });
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleCast(HandlerContext handlerContext)
            => handlerContext.EvalOnClient(requiresClientResultOperator: false);

        private static Expression HandleContains(HandlerContext handlerContext)
        {
            var filteringVisitor = handlerContext.CreateSqlTranslatingVisitor(bindParentQueries: true);

            var itemResultOperator = (ContainsResultOperator)handlerContext.ResultOperator;

            var item = filteringVisitor.Visit(itemResultOperator.Item);
            if (item != null)
            {
                var itemSelectExpression = item as SelectExpression;

                if (itemSelectExpression != null)
                {
                    var entityType = handlerContext.Model.FindEntityType(handlerContext.QueryModel.MainFromClause.ItemType);

                    if (entityType != null)
                    {
                        var outerSelectExpression = handlerContext.SelectExpressionFactory.Create(handlerContext.QueryModelVisitor.QueryCompilationContext);
                        outerSelectExpression.SetProjectionExpression(Expression.Constant(1));

                        var collectionSelectExpression
                            = handlerContext.SelectExpression.Clone(handlerContext.QueryModelVisitor.QueryCompilationContext.CreateUniqueTableAlias());
                        outerSelectExpression.AddTable(collectionSelectExpression);

                        itemSelectExpression.Alias = handlerContext.QueryModelVisitor.QueryCompilationContext.CreateUniqueTableAlias();
                        var joinExpression = outerSelectExpression.AddInnerJoin(itemSelectExpression);

                        foreach (var property in entityType.FindPrimaryKey().Properties)
                        {
                            itemSelectExpression.AddToProjection(
                                new ColumnExpression(
                                    property.Name,
                                    property,
                                    itemSelectExpression.Tables.First()));

                            collectionSelectExpression.AddToProjection(
                                new ColumnExpression(
                                    property.Name,
                                    property,
                                    collectionSelectExpression.Tables.First()));

                            var predicate = Expression.Equal(
                                new ColumnExpression(
                                    property.Name,
                                    property,
                                    collectionSelectExpression),
                                new ColumnExpression(
                                    property.Name,
                                    property,
                                    itemSelectExpression));

                            joinExpression.Predicate
                                = joinExpression.Predicate == null
                                    ? predicate
                                    : Expression.AndAlso(
                                        joinExpression.Predicate,
                                        predicate);
                        }

                        SetProjectionConditionalExpression(
                            handlerContext,
                            Expression.Condition(
                                new ExistsExpression(outerSelectExpression),
                                Expression.Constant(true),
                                Expression.Constant(false),
                                typeof(bool)));

                        return TransformClientExpression<bool>(handlerContext);
                    }
                }

                SetProjectionConditionalExpression(
                    handlerContext,
                    Expression.Condition(
                        new InExpression(
                            item,
                            handlerContext.SelectExpression.Clone("")),
                        Expression.Constant(true),
                        Expression.Constant(false),
                        typeof(bool)));

                return TransformClientExpression<bool>(handlerContext);
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleCount(HandlerContext handlerContext)
        {
            if (handlerContext.SelectExpression.Offset != null)
            {
                handlerContext.SelectExpression.PushDownSubquery();
            }

            handlerContext.SelectExpression
                .SetProjectionExpression(
                    new SqlFunctionExpression(
                        "COUNT",
                        typeof(int),
                        new[] { new SqlFragmentExpression("*") }));

            handlerContext.SelectExpression.ClearOrderBy();

            return TransformClientExpression<int>(handlerContext);
        }

        private static Expression HandleDefaultIfEmpty(HandlerContext handlerContext)
        {
            var defaultIfEmptyResultOperator = (DefaultIfEmptyResultOperator)handlerContext.ResultOperator;

            if (defaultIfEmptyResultOperator.OptionalDefaultValue != null)
            {
                return handlerContext.EvalOnClient();
            }

            var selectExpression = handlerContext.SelectExpression;

            selectExpression.PushDownSubquery();
            selectExpression.ExplodeStarProjection();

            var subquery = selectExpression.Tables.Single();

            selectExpression.ClearTables();

            var emptySelectExpression = handlerContext.SelectExpressionFactory.Create(handlerContext.QueryModelVisitor.QueryCompilationContext, "empty");
            emptySelectExpression.AddToProjection(new AliasExpression("empty", Expression.Constant(null)));

            selectExpression.AddTable(emptySelectExpression);

            var leftOuterJoinExpression = new LeftOuterJoinExpression(subquery);
            var constant1 = Expression.Constant(1);

            leftOuterJoinExpression.Predicate = Expression.Equal(constant1, constant1);

            selectExpression.AddTable(leftOuterJoinExpression);

            selectExpression.ProjectStarAlias = subquery.Alias;

            handlerContext.QueryModelVisitor.Expression
                = new DefaultIfEmptyExpressionVisitor(
                        handlerContext.QueryModelVisitor.QueryCompilationContext)
                    .Visit(handlerContext.QueryModelVisitor.Expression);

            return handlerContext.EvalOnClient(requiresClientResultOperator: false);
        }

        private sealed class DefaultIfEmptyExpressionVisitor : ExpressionVisitor
        {
            private readonly RelationalQueryCompilationContext _relationalQueryCompilationContext;

            public DefaultIfEmptyExpressionVisitor(RelationalQueryCompilationContext relationalQueryCompilationContext)
            {
                _relationalQueryCompilationContext = relationalQueryCompilationContext;
            }

            protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
            {
                if (methodCallExpression.Method.MethodIsClosedFormOf(
                    _relationalQueryCompilationContext.QueryMethodProvider.ShapedQueryMethod))
                {
                    return Expression.Call(
                        _relationalQueryCompilationContext.QueryMethodProvider.DefaultIfEmptyShapedQueryMethod
                            .MakeGenericMethod(methodCallExpression.Method.GetGenericArguments()),
                        methodCallExpression.Arguments);
                }

                if (methodCallExpression.Method.MethodIsClosedFormOf(
                    _relationalQueryCompilationContext.QueryMethodProvider.InjectParametersMethod))
                {
                    var newSource = VisitMethodCall((MethodCallExpression)methodCallExpression.Arguments[1]);

                    return Expression.Call(
                        methodCallExpression.Method,
                        methodCallExpression.Arguments[0],
                        newSource,
                        methodCallExpression.Arguments[2],
                        methodCallExpression.Arguments[3]);
                }

                return base.VisitMethodCall(methodCallExpression);
            }
        }

        private static Expression HandleDistinct(HandlerContext handlerContext)
        {
            if (!handlerContext.QueryModelVisitor.RequiresClientProjection)
            {
                var selectExpression = handlerContext.SelectExpression;

                selectExpression.IsDistinct = true;

                if (selectExpression.OrderBy.Any(o =>
                    {
                        var orderByColumnExpression = o.Expression.TryGetColumnExpression();

                        if (orderByColumnExpression == null)
                        {
                            return true;
                        }

                        return !selectExpression.Projection.Any(e =>
                            {
                                var projectionColumnExpression = e.TryGetColumnExpression();

                                return projectionColumnExpression != null
                                       && projectionColumnExpression.Equals(orderByColumnExpression);
                            });
                    }))
                {
                    handlerContext.SelectExpression.ClearOrderBy();
                }

                return handlerContext.EvalOnServer;
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleFirst(HandlerContext handlerContext)
        {
            handlerContext.SelectExpression.Limit = Expression.Constant(1);

            var requiresClientResultOperator = !((FirstResultOperator)handlerContext.ResultOperator).ReturnDefaultWhenEmpty
                                               && handlerContext.QueryModelVisitor.ParentQueryModelVisitor != null;

            return handlerContext.EvalOnClient(requiresClientResultOperator);
        }

        private static Expression HandleGroup(HandlerContext handlerContext)
        {
            var sqlTranslatingExpressionVisitor = handlerContext.CreateSqlTranslatingVisitor();

            var groupResultOperator = (GroupResultOperator)handlerContext.ResultOperator;

            var sqlExpression
                = sqlTranslatingExpressionVisitor.Visit(groupResultOperator.KeySelector);

            if (sqlExpression != null)
            {
                handlerContext.SelectExpression.ClearOrderBy();

                var columns = (sqlExpression as ConstantExpression)?.Value as Expression[];

                if (columns != null)
                {
                    foreach (var column in columns)
                    {
                        handlerContext.SelectExpression
                            .AddToOrderBy(new Ordering(column, OrderingDirection.Asc));
                    }
                }
                else
                {
                    handlerContext.SelectExpression
                        .AddToOrderBy(new Ordering(sqlExpression, OrderingDirection.Asc));
                }
            }

            var oldGroupByCall = (MethodCallExpression)handlerContext.EvalOnClient();

            return sqlExpression != null
                ? Expression.Call(handlerContext.QueryModelVisitor.QueryCompilationContext.QueryMethodProvider.GroupByMethod
                        .MakeGenericMethod(oldGroupByCall.Method.GetGenericArguments()),
                    oldGroupByCall.Arguments)
                : oldGroupByCall;
        }

        private static Expression HandleLast(HandlerContext handlerContext)
        {
            var requiresClientResultOperator = true;
            if (handlerContext.SelectExpression.OrderBy.Any())
            {
                foreach (var ordering in handlerContext.SelectExpression.OrderBy)
                {
                    ordering.OrderingDirection
                        = ordering.OrderingDirection == OrderingDirection.Asc
                            ? OrderingDirection.Desc
                            : OrderingDirection.Asc;
                }

                handlerContext.SelectExpression.Limit = Expression.Constant(1);
                requiresClientResultOperator = false;
            }

            requiresClientResultOperator
                = requiresClientResultOperator
                || (!((LastResultOperator)handlerContext.ResultOperator).ReturnDefaultWhenEmpty
                    && handlerContext.QueryModelVisitor.ParentQueryModelVisitor != null);

            return handlerContext.EvalOnClient(requiresClientResultOperator: requiresClientResultOperator);
        }

        private static Expression HandleLongCount(HandlerContext handlerContext)
        {
            if (handlerContext.SelectExpression.Offset != null)
            {
                handlerContext.SelectExpression.PushDownSubquery();
            }

            handlerContext.SelectExpression
                .SetProjectionExpression(
                    new SqlFunctionExpression(
                        "COUNT",
                        typeof(long),
                        new[] { new SqlFragmentExpression("*") }));

            handlerContext.SelectExpression.ClearOrderBy();

            return TransformClientExpression<long>(handlerContext);
        }

        private static Expression HandleMin(HandlerContext handlerContext)
        {
            if (!handlerContext.QueryModelVisitor.RequiresClientProjection
                && handlerContext.SelectExpression.Projection.Count == 1)
            {
                var expression = handlerContext.SelectExpression.Projection.First();
                var minExpression = new SqlFunctionExpression("MIN", expression.Type, new[] { expression });

                handlerContext.SelectExpression.SetProjectionExpression(minExpression);

                return (Expression)_transformClientExpressionMethodInfo
                    .MakeGenericMethod(minExpression.Type)
                    .Invoke(null, new object[] { handlerContext });
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleMax(HandlerContext handlerContext)
        {
            if (!handlerContext.QueryModelVisitor.RequiresClientProjection
                && handlerContext.SelectExpression.Projection.Count == 1)
            {
                var expression = handlerContext.SelectExpression.Projection.First();
                var maxExpression = new SqlFunctionExpression("MAX", expression.Type, new[] { expression });

                handlerContext.SelectExpression.SetProjectionExpression(maxExpression);

                return (Expression)_transformClientExpressionMethodInfo
                    .MakeGenericMethod(maxExpression.Type)
                    .Invoke(null, new object[] { handlerContext });
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleSingle(HandlerContext handlerContext)
        {
            handlerContext.SelectExpression.Limit = Expression.Constant(2);

            var returnExpression = handlerContext.EvalOnClient(requiresClientResultOperator: true);

            // For top level single, we do not require client eval
            if (handlerContext.QueryModelVisitor.ParentQueryModelVisitor == null)
            {
                handlerContext.QueryModelVisitor.RequiresClientResultOperator = false;
            }

            return returnExpression;
        }

        private static Expression HandleSkip(HandlerContext handlerContext)
        {
            var skipResultOperator = (SkipResultOperator)handlerContext.ResultOperator;

            var sqlTranslatingExpressionVisitor
                = handlerContext.CreateSqlTranslatingVisitor(bindParentQueries: true);

            var offset = sqlTranslatingExpressionVisitor.Visit(skipResultOperator.Count);
            if (offset != null)
            {
                handlerContext.SelectExpression.Offset = offset;

                return handlerContext.EvalOnServer;
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleSum(HandlerContext handlerContext)
        {
            if (!handlerContext.QueryModelVisitor.RequiresClientProjection
                && handlerContext.SelectExpression.Projection.Count == 1)
            {
                var expression = handlerContext.SelectExpression.Projection.First();
                var sumExpression = new SqlFunctionExpression("SUM", expression.Type, new[] { expression });

                handlerContext.SelectExpression.SetProjectionExpression(sumExpression);

                return (Expression)_transformClientExpressionMethodInfo
                    .MakeGenericMethod(sumExpression.Type)
                    .Invoke(null, new object[] { handlerContext });
            }

            return handlerContext.EvalOnClient();
        }

        private static Expression HandleTake(HandlerContext handlerContext)
        {
            var takeResultOperator = (TakeResultOperator)handlerContext.ResultOperator;

            var sqlTranslatingExpressionVisitor
                = handlerContext.CreateSqlTranslatingVisitor(bindParentQueries: true);

            var limit = sqlTranslatingExpressionVisitor.Visit(takeResultOperator.Count);

            if (limit != null)
            {
                handlerContext.SelectExpression.Limit = limit;

                return handlerContext.EvalOnServer;
            }

            return handlerContext.EvalOnClient();
        }

        private static void SetProjectionConditionalExpression(
            HandlerContext handlerContext, ConditionalExpression conditionalExpression)
        {
            handlerContext.SelectExpression.SetProjectionConditionalExpression(conditionalExpression);
            handlerContext.SelectExpression.ClearTables();
            handlerContext.SelectExpression.ClearOrderBy();
            handlerContext.SelectExpression.Offset = null;
            handlerContext.SelectExpression.Limit = null;
            handlerContext.SelectExpression.Predicate = null;
        }

        private static readonly MethodInfo _transformClientExpressionMethodInfo
            = typeof(RelationalResultOperatorHandler).GetTypeInfo()
                .GetDeclaredMethod(nameof(TransformClientExpression));

        private static Expression TransformClientExpression<TResult>(HandlerContext handlerContext)
        {
            var querySource
                = handlerContext.QueryModel.BodyClauses
                      .OfType<IQuerySource>()
                      .LastOrDefault()
                  ?? handlerContext.QueryModel.MainFromClause;

            var visitor
                = new ResultTransformingExpressionVisitor<TResult>(
                    querySource,
                    handlerContext.QueryModelVisitor.QueryCompilationContext);

            return visitor.Visit(handlerContext.QueryModelVisitor.Expression);
        }
    }
}
