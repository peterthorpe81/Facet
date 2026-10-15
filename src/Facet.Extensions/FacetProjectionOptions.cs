using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Facet.Extensions;

/// <summary>Base state and expression-tree support for generated navigation projection options.</summary>
public class FacetProjectionOptions
{
    private readonly Dictionary<string, FacetProjectionOptions> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LambdaExpression?> _includes = new(StringComparer.Ordinal);

    /// <summary>Registers a generated child options object for a navigation member.</summary>
    public void RegisterChild(string memberName, FacetProjectionOptions child) => _children[memberName] = child;

    /// <summary>Marks a navigation member for projection and optionally supplies a collection filter.</summary>
    public void Include(string memberName, LambdaExpression? filter = null)
    {
        if (filter != null || !_includes.ContainsKey(memberName))
            _includes[memberName] = filter;
    }

    /// <summary>Gets the generated child options for a navigation member.</summary>
    public FacetProjectionOptions? GetChild(string memberName) => _children.TryGetValue(memberName, out var child) ? child : null;

    /// <summary>Returns whether the navigation member was explicitly selected.</summary>
    public bool IsIncluded(string memberName) => _includes.ContainsKey(memberName);

    /// <summary>Gets an optional collection filter.</summary>
    public LambdaExpression? GetFilter(string memberName) => _includes.TryGetValue(memberName, out var filter) ? filter : null;

    internal bool HasSelections => _includes.Count != 0 || _children.Values.Any(child => child.HasSelections);

    /// <summary>Applies the selected navigation paths and filters to a generated projection.</summary>
    public static Expression<Func<TSource, TTarget>> Apply<TSource, TTarget>(
        Expression<Func<TSource, TTarget>> projection,
        FacetProjectionOptions options)
    {
        if (projection is null) throw new ArgumentNullException(nameof(projection));
        if (options is null) throw new ArgumentNullException(nameof(options));

        return Expression.Lambda<Func<TSource, TTarget>>(
            new ProjectionOptionsVisitor(options).Visit(projection.Body)!,
            projection.Parameters);
    }

    private sealed class ProjectionOptionsVisitor : ExpressionVisitor
    {
        private readonly FacetProjectionOptions _options;

        public ProjectionOptionsVisitor(FacetProjectionOptions options) => _options = options;

        protected override Expression VisitMemberInit(MemberInitExpression node)
        {
            var bindings = new List<MemberBinding>(node.Bindings.Count);
            foreach (var binding in node.Bindings)
            {
                if (binding is not MemberAssignment assignment || !_options._children.TryGetValue(binding.Member.Name, out var child))
                {
                    bindings.Add(VisitMemberBinding(binding));
                    continue;
                }

                if (!_options.IsIncluded(binding.Member.Name) && !child.HasSelections)
                {
                    var propertyType = ((PropertyInfo)binding.Member).PropertyType;
                    bindings.Add(Expression.Bind(binding.Member, Expression.Default(propertyType)));
                    continue;
                }

                var rewritten = RewriteNavigation(assignment.Expression, child, _options.GetFilter(binding.Member.Name));
                bindings.Add(Expression.Bind(binding.Member, rewritten));
            }

            return Expression.MemberInit((NewExpression)Visit(node.NewExpression)!, bindings);
        }

        private static Expression RewriteNavigation(Expression expression, FacetProjectionOptions child, LambdaExpression? filter)
        {
            if (expression is ConditionalExpression conditional)
            {
                return Expression.Condition(
                    conditional.Test,
                    RewriteNavigation(conditional.IfTrue, child, filter),
                    conditional.IfFalse);
            }

            if (expression is MethodCallExpression call && call.Arguments.Count == 2 && call.Method.Name == "Select")
            {
                var source = call.Arguments[0];
                var selectorExpression = Unquote(call.Arguments[1]);
                if (selectorExpression is LambdaExpression selector)
                {
                    if (filter != null)
                        source = AddWhere(source, filter);

                    var transformedBody = new ProjectionOptionsVisitor(child).Visit(selector.Body)!;
                    var transformedSelector = Expression.Lambda(selector.Type, transformedBody, selector.Parameters);
                    var arguments = call.Arguments.ToArray();
                    arguments[0] = source;
                    arguments[1] = call.Method.GetParameters()[1].ParameterType.IsGenericType &&
                                   call.Method.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>)
                        ? Expression.Quote(transformedSelector)
                        : transformedSelector;
                    return Expression.Call(call.Object, call.Method, arguments);
                }
            }

            if (expression is MethodCallExpression wrapper)
            {
                var args = wrapper.Arguments.Select((arg, index) => index == 0
                    ? RewriteNavigation(arg, child, filter)
                    : arg).ToArray();
                return Expression.Call(wrapper.Object, wrapper.Method, args);
            }

            return new ProjectionOptionsVisitor(child).Visit(expression)!;
        }

        private static Expression AddWhere(Expression source, LambdaExpression filter)
        {
            var elementType = filter.Parameters[0].Type;
            var whereMethod = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(method => method.Name == nameof(Enumerable.Where) && method.GetParameters().Length == 2 &&
                    method.GetParameters()[1].ParameterType.IsGenericType &&
                    method.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>))
                .MakeGenericMethod(elementType);
            return Expression.Call(whereMethod, source, filter);
        }

        private static Expression Unquote(Expression expression) =>
            expression.NodeType == ExpressionType.Quote ? ((UnaryExpression)expression).Operand : expression;
    }
}