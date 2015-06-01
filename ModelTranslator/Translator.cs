﻿namespace ModelTranslator
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    using ModelTranslator.Model;
    using ModelTranslator.Utils;


    class Translator
    {
        /// <summary>
        /// Translate class definitions from C# to TypeScript.
        /// </summary>
        public string Translate(string src)
        {
            var tree = CSharpSyntaxTree.ParseText(src);
            var classModel = ParseModel(tree);
            var result = RenderModel(classModel);
            return result;
        }

        #region Create model

        /// <summary>
        /// Parses a class in the source file into intermediate representation.
        /// </summary>
        private ClassModel ParseModel(SyntaxTree tree)
        {
            var classNode = tree.GetRoot()
                                .DescendantNodes()
                                .OfType<ClassDeclarationSyntax>()
                                .Single();

            var baseTypes = classNode.BaseList.Types.OfType<SimpleBaseTypeSyntax>()
                                     .Select(x => x.Type)
                                     .Where(x => x is IdentifierNameSyntax || x is GenericNameSyntax)
                                     .Select(x => x.ToString())
                                     .ToArray();

            var classModel = new ClassModel
            {
                Name = classNode.Identifier.Text,
                BaseType = baseTypes.FirstOrDefault(x => !x.StartsWith("I")),
                Interfaces = baseTypes.Where(x => x.StartsWith("I")).ToList(),

                Comment = ParseComment(classNode),

                Constructor = ParseConstructor(classNode),
                Fields = ParseFields(classNode).ToList(),
                Properties = ParseProperties(classNode).ToList(),
                Methods = ParseMethods(classNode).ToList()
            };

            return classModel;
        }

        /// <summary>
        /// Returns the list of fields in the class.
        /// </summary>
        private IEnumerable<FieldModel> ParseFields(ClassDeclarationSyntax classNode)
        {
            var fields = classNode.DescendantNodes()
                                  .OfType<FieldDeclarationSyntax>();

            foreach (var field in fields)
            {
                var decl = field.Declaration;
                var nameSyntax = decl.Variables.First();
                yield return new FieldModel
                {
                    Name = nameSyntax.Identifier.ToString(),
                    Type = decl.Type.ToString(),
                    InitializerCode = nameSyntax.Initializer != null ? nameSyntax.Initializer.Value.ToString() : null,
                    Comment = ParseComment(field)
                };
            }
        }

        /// <summary>
        /// Returns the list of properties in the class.
        /// </summary>
        private IEnumerable<PropertyModel> ParseProperties(ClassDeclarationSyntax classNode)
        {
            var properties = classNode.DescendantNodes()
                                      .OfType<PropertyDeclarationSyntax>();

            foreach (var pty in properties)
            {
                var accessors = pty.AccessorList.Accessors;
                var setter = accessors.FirstOrDefault(x => x.Keyword.Kind() == SyntaxKind.SetKeyword);

                yield return new PropertyModel
                {
                    Type = pty.Type.ToString(),
                    Name = pty.Identifier.Text,
                    HasSetter = setter != null,
                    Comment = ParseComment(pty)
                };
            }
        }

        /// <summary>
        /// Returns the list of methods in the class.
        /// </summary>
        private IEnumerable<MethodModel> ParseMethods(ClassDeclarationSyntax classNode)
        {
            var methods = classNode.DescendantNodes()
                                   .OfType<MethodDeclarationSyntax>();

            foreach (var method in methods)
            {
                var args = method.ParameterList.Parameters;
                yield return new MethodModel
                {
                    Name = method.Identifier.Text,
                    Type = method.ReturnType.ToString(),
                    IsPrivate = method.Modifiers.Any(SyntaxKind.PrivateKeyword),

                    Arguments = args.Select(x => new ArgumentModel
                    {
                        Name = x.Identifier.Text,
                        Type = x.Type.ToString(),
                        InitializerCode = x.Default != null ? x.Default.Value.ToString() : null
                    }).ToList(),

                    Comment = ParseComment(method),
                };
            }
        }

        /// <summary>
        /// Returns the constructor definition.
        /// </summary>
        private ConstructorModel ParseConstructor(ClassDeclarationSyntax classNode)
        {
            var ctorNode = classNode.DescendantNodes()
                                    .OfType<ConstructorDeclarationSyntax>()
                                    .FirstOrDefault();

            if (ctorNode == null)
                return null;

            var args = ctorNode.ParameterList.Parameters;
            var baseCallArgs = ctorNode.Initializer.ArgumentList.Arguments;

            return new ConstructorModel
            {
                Arguments = args.Select(x => new ArgumentModel
                {
                    Name = x.Identifier.Text,
                    Type = x.Type.ToString(),
                    InitializerCode = x.Default != null ? x.Default.Value.ToString() : null
                })
                .ToList(),

                BaseCall = baseCallArgs.Select(x => new ArgumentModel { Name = x.Expression.ToString() }).ToList(),
                Comment = ParseComment(ctorNode)
            };
        }

        #endregion

        #region Comment parsing

        private static readonly Regex CommentSpaceRegex = new Regex("\n\\s+", RegexOptions.Compiled);
        private static readonly Regex CommentSummaryRegex = new Regex("<summary>(?<text>.+)</summary>", RegexOptions.Compiled | RegexOptions.ExplicitCapture | RegexOptions.Singleline);

        /// <summary>
        /// Returns the corresponding documentation comment.
        /// </summary>
        private string ParseComment(SyntaxNode node)
        {
            var trivia = node.GetLeadingTrivia().FirstOrDefault(x => x.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));
            return trivia.FullSpan.IsEmpty ? null : "/// " + CommentSpaceRegex.Replace(trivia.ToString().Trim(), "\n");
        }

        /// <summary>
        /// Returns the bare text from comment.
        /// </summary>
        private string GetCommentSummary(string comment)
        {
            if(string.IsNullOrEmpty(comment))
                return null;

            var rawValue = CommentSummaryRegex.Match(comment).Groups["text"].Value.Replace("///", "").Replace('\n', ' ').Trim();
            return rawValue;
        }

        /// <summary>
        /// Removes descriptions for inexistant arguments.
        /// </summary>
        private string CleanUpComment(string comment, IEnumerable<string> restrictedArgs)
        {
            return restrictedArgs.Aggregate(
                comment,
                (current, restrictedArg) => new Regex(
                    string.Format(@"/// <param name=""{0}"">(.+?)</param>\n?", restrictedArg),
                    RegexOptions.Singleline
                ).Replace(current, "")
            );
        }

        #endregion

        #region Name translation

        /// <summary>
        /// Applies a naming convention to field or property name.
        /// </summary>
        private string ApplyNameConvention(string name, bool isPrivate)
        {
            name = name.TrimStart('_');
            name = name.Substring(0, 1).ToLowerInvariant() + name.Substring(1);
            if (isPrivate)
                name = '_' + name;

            if (name.EndsWith("Subject"))
                return name.Substring(0, name.Length - "Subject".Length);

            if (name == "_subscribings")
                return "_subscriptions";

            return name;
        }

        private readonly Regex ListTypeConvention = new Regex(@"^List<(?<name>.+)>$", RegexOptions.Compiled | RegexOptions.ExplicitCapture);
        private readonly Regex SubjectTypeConvention = new Regex(@"^Subject<(?<name>.+)>$", RegexOptions.Compiled | RegexOptions.ExplicitCapture);
        private readonly Regex GenericTypeConvention = new Regex(@"^(?<type>[a-z][a-z0-9]*)<(?<name>.+)>$", RegexOptions.Compiled | RegexOptions.ExplicitCapture);
        private readonly Regex InitializerNewObjectConvention = new Regex(@"new (?<type>.+)\s\((?<args>.*)\)", RegexOptions.Compiled | RegexOptions.ExplicitCapture);

        private readonly Dictionary<string, string> BasicTypes = new Dictionary<string, string>
        {
            { "int", "number" },
            { "long", "number" },
            { "float", "number" },
            { "double", "number" },
            { "object", "any" },
            { "bool", "boolean" },
        };

        /// <summary>
        /// Applies a naming convention to types.
        /// </summary>
        private string ApplyTypeConvention(string type, bool useInterface = true)
        {
            if (BasicTypes.ContainsKey(type))
                return BasicTypes[type];

            var listMatch = ListTypeConvention.Match(type);
            if (listMatch.Success)
                return ApplyTypeConvention(listMatch.Groups["name"].Value) + "[]";

            var subjectMatch = SubjectTypeConvention.Match(type);
            if (subjectMatch.Success)
                return (useInterface ? "I" : "") + "Observable<" + ApplyTypeConvention(subjectMatch.Groups["name"].Value) + ">";

            var genericMatch = GenericTypeConvention.Match(type);
            if (genericMatch.Success)
                return ApplyTypeConvention(listMatch.Groups["type"].Value) + "<" + ApplyTypeConvention(listMatch.Groups["name"].Value) + ">";

            return type;
        }

        /// <summary>
        /// Attempts to translate initializers.
        /// </summary>
        private string ApplyInitializerConvention(string value)
        {
            var initMatch = InitializerNewObjectConvention.Match(value);
            if (initMatch.Success)
                return string.Format("new {0} ({1})", ApplyTypeConvention(initMatch.Groups["type"].Value), initMatch.Groups["args"].Value);

            return value;
        }

        #endregion

        #region Render model to typescript

        /// <summary>
        /// Renders the model as Typescript code.
        /// </summary>
        private string RenderModel(ClassModel model)
        {
            var sb = new SourceBuilder();

            using (var line = sb.Line())
            {
                // class <X> [extends <Y>] [implements <Z1, Z2, ...>] {
                line.Append("class {0}", model.Name);
                if (!string.IsNullOrEmpty(model.BaseType))
                    line.Append("extends {0}", model.BaseType);
                if (model.Interfaces.Any())
                    line.Append("implements {0}", string.Join(", ", model.Interfaces));
            }

            using (sb.NestedBlock())
            {
                if (!string.IsNullOrEmpty(model.Comment))
                    using(sb.SpacedBlock())
                        sb.Append(model.Comment);

                AppendFields(sb, model);
                AppendConstructor(sb, model);
                AppendProperties(sb, model);
                AppendEvents(sb, model);
                AppendMethods(sb, model);
                AppendDisposableBlock(sb, model);
                AppendEquatableBlock(sb, model);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Writes the block of field declarations with comments, in alphabetical order, excluding special names.
        /// </summary>
        private void AppendFields(SourceBuilder sb, ClassModel model)
        {
            // add backing fields for properties
            var fieldsLookup = model.Fields.ToDictionary(x => x.Name, x => x);
            foreach (var pty in model.Properties)
            {
                var name = ApplyNameConvention(pty.Name, true);
                if(!fieldsLookup.ContainsKey(name))
                    fieldsLookup.Add(name, new FieldModel { Name = pty.Name, Type = pty.Type, Comment = pty.Comment });
            }

            var restrictions = new Func<FieldModel, bool>[] { x => x.Name == "_isDisposed", x => x.Type == "ILogService" };
            var allFields = fieldsLookup.Values.Restrict(restrictions).ToList();

            if (!fieldsLookup.Any())
                return;

            sb.AppendRegionHeader("Fields");

            foreach (var field in allFields.OrderBy(x => x.Name))
            {
                using (sb.SpacedBlock())
                {
                    sb.Append("// " + GetCommentSummary(field.Comment));
                    sb.Append("private {0}: {1};", ApplyNameConvention(field.Name, true), ApplyTypeConvention(field.Type));
                }
            }
        }

        /// <summary>
        /// Creates a default constructor body.
        /// </summary>
        private void AppendConstructor(SourceBuilder sb, ClassModel model)
        {
            sb.AppendRegionHeader("Constructor");

            var restrictions = new Func<ArgumentModel, bool>[] { x => x.Type == "ILogService" };
            var argDefs = model.Constructor.Arguments.Restrict(restrictions).ToList();
            sb.Append("constructor ({0}) ", BuildArgumentList(argDefs));

            using (sb.NestedBlock())
            {
                // documentation comment
                var restrictedArgs = model.Constructor.Arguments.Where(x => !argDefs.Any(y => y.Name == x.Name)).Select(x => x.Name);
                using(sb.SpacedBlock())
                    sb.Append(CleanUpComment(model.Constructor.Comment, restrictedArgs));

                // base call
                if (model.Constructor.BaseCall.Count > 0)
                {
                    var baseCallArgs = model.Constructor.BaseCall.Select(x => x.Name);
                    using(sb.SpacedBlock())
                        sb.Append("super({0});", string.Join(", ", baseCallArgs));
                }

                // todo: contracts?

                // default values for fields
                using (sb.SpacedBlock())
                foreach (var field in model.Fields)
                {
                    if (string.IsNullOrEmpty(field.InitializerCode))
                        continue;

                    sb.Append("this.{0} = {1};", ApplyNameConvention(field.Name, true), ApplyInitializerConvention(field.InitializerCode));
                }

                // field init
                using (sb.SpacedBlock())
                foreach (var arg in argDefs)
                {
                    if (model.Fields.Any(x => ApplyNameConvention(x.Name, true) == "_" + arg.Name) || model.Properties.Any(x => ApplyNameConvention(x.Name, false) == arg.Name))
                        sb.Append("this._{0} = {0};", arg.Name);
                }

                using (sb.SpacedBlock())
                sb.Append("// TODO: custom initialization code here");
            }
        }

        /// <summary>
        /// Appends the list of simple properties.
        /// </summary>
        private void AppendProperties(SourceBuilder sb, ClassModel model)
        {
            var restrictions = new Func<PropertyModel, bool>[] { x => x.Type.StartsWith("Subject") };
            var ptys = model.Properties.Restrict(restrictions).ToList();
        }

        /// <summary>
        /// Appends the list of event properties.
        /// </summary>
        private void AppendEvents(SourceBuilder sb, ClassModel model)
        {
            var restrictions = new Func<PropertyModel, bool>[] { x => !x.Type.StartsWith("Subject") };
            var handlers = model.Properties.Restrict(restrictions).ToList();

            if (handlers.Count == 0)
                return;

            sb.AppendRegionHeader("Event handlers");

            foreach (var handler in handlers)
            {
                using (sb.SpacedBlock())
                {
                    using (var line = sb.Line())
                    {
                        line.Append("get {0}()", ApplyNameConvention(handler.Name, false));
                        if(handler.Type != "void")
                            line.Append(": {0}", ApplyTypeConvention(handler.Type));
                    }

                    using (sb.NestedBlock())
                    {
                        using (sb.SpacedBlock())
                        sb.Append(handler.Comment);

                        using (sb.SpacedBlock())
                        sb.Append("return this._{0}", ApplyNameConvention(handler.Name, true));
                    }
                }
            }
        }

        /// <summary>
        /// Appends the list of methods.
        /// </summary>
        private void AppendMethods(SourceBuilder sb, ClassModel model)
        {
            var restrictedNames = new[] { "Dispose", "Equals", "GetHashCode", "ObjectInvariant" };
            var restrictions = new Func<MethodModel, bool>[]{ x => restrictedNames.Contains(x.Name) };
            var methods = model.Methods.Restrict(restrictions)
                               .GroupBy(x => x.IsPrivate && x.Type == "void" && x.Arguments.Count == 1)
                               .ToDictionary(x => x.Key, x => x.ToList());

            var regions = new Dictionary<bool, string>() { { false, "Methods" }, { true, "Event handlers" } };
            foreach (var region in regions)
            {
                List<MethodModel> regionMethods;
                if (!methods.TryGetValue(region.Key, out regionMethods) || !regionMethods.Any())
                    continue;

                sb.AppendRegionHeader(region.Value);

                foreach (var method in regionMethods)
                {
                    using (sb.SpacedBlock())
                    {
                        using (var line = sb.Line())
                        {
                            if (method.IsPrivate)
                                line.Append("private");

                            line.Append(
                                "{0} ({1})",
                                method.Name,
                                BuildArgumentList(method.Arguments)
                            );

                            if (method.Type != "void")
                                line.Append(": {0}", ApplyTypeConvention(method.Type));
                        }

                        using (sb.NestedBlock())
                        {
                            using (sb.SpacedBlock())
                            sb.Append(method.Comment);

                            using (sb.SpacedBlock())
                            sb.Append("// TODO: method body");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Appends a default "IDisposable" implementation.
        /// </summary>
        private void AppendDisposableBlock(SourceBuilder sb, ClassModel model)
        {
            if (!model.Interfaces.Contains("IDisposable"))
                return;

            sb.AppendRegionHeader("IDisposable implementation");

            using (sb.SpacedBlock())
            sb.Append("private _isDisposed: boolean;");

            sb.Append("dispose()");
            using (sb.NestedBlock())
            {
                sb.Append("if(this._isDisposed)");
                using (sb.NestedBlock())
                    sb.Append("return;");

                using (sb.SpacedBlock())
                foreach (var field in model.Fields)
                    if (field.Type == "CompositeDisposable" || field.Type.StartsWith("IObservable"))
                        sb.Append("this.{0}.dispose();", ApplyNameConvention(field.Name, true));

                using (sb.SpacedBlock())
                sb.Append("// TODO: custom code here");

                using (sb.SpacedBlock())
                sb.Append("this._isDisposed = true;");
            }
        }

        /// <summary>
        /// Appends an empty "IEquatable" implementation.
        /// </summary>
        private void AppendEquatableBlock(SourceBuilder sb, ClassModel model)
        {
            if (!model.Interfaces.Any(x => x.StartsWith("IEquatable")))
                return;

            sb.AppendRegionHeader("IEquatable implementation");
        }

        /// <summary>
        /// Creates a comma-separated list of argument declarations.
        /// </summary>
        private string BuildArgumentList(IEnumerable<ArgumentModel> args)
        {
            var argStrings = args.Select(x => string.Format(
                "{0}: {1}{2}",
                x.Name,
                ApplyTypeConvention(x.Type),
                string.IsNullOrEmpty(x.InitializerCode)
                    ? ""
                    : " = " + ApplyInitializerConvention(x.InitializerCode)
            ));
            return string.Join(", ", argStrings);
        }

        #endregion
    }
}
