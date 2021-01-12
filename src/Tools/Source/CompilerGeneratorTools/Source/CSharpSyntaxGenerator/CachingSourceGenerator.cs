﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// We only build the Source Generator in the netstandard target
#if NETSTANDARD

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CSharpSyntaxGenerator
{
    public abstract class CachingSourceGenerator : ISourceGenerator
    {
        /// <summary>
        /// ⚠ This value may be accessed by multiple threads.
        /// </summary>
        private static readonly WeakReference<CachedSourceGeneratorResult> s_cachedResult = new(null);

        protected abstract bool TryGetRelevantInput(in GeneratorExecutionContext context, out AdditionalText? input, out SourceText? inputText);

        protected abstract bool TryGenerateSources(
            AdditionalText input,
            SourceText inputText,
            out ImmutableArray<(string hintName, SourceText sourceText)> sources,
            out ImmutableArray<Diagnostic> diagnostics,
            CancellationToken cancellationToken);

        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (!TryGetRelevantInput(in context, out var input, out var inputText))
            {
                return;
            }

            // Get the current input checksum, which will either be used for verifying the current cache or updating it
            // with the new results.
            var currentChecksum = inputText.GetChecksum();

            // Read the current cached result once to avoid race conditions
            if (s_cachedResult.TryGetTarget(out var cachedResult)
                && cachedResult.Checksum.SequenceEqual(currentChecksum))
            {
                AddSources(in context, sources: cachedResult.Sources, currentChecksum, CacheBehavior.None);
                return;
            }

            if (TryGenerateSources(input, inputText, out var sources, out var diagnostics, context.CancellationToken))
            {
                AddSources(in context, sources, currentChecksum, diagnostics.IsEmpty ? CacheBehavior.Update : CacheBehavior.Clear);
            }
            else
            {
                s_cachedResult.SetTarget(null);
            }

            // Always report the diagnostics (if any)
            foreach (var diagnostic in diagnostics)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        private static void AddSources(
            in GeneratorExecutionContext context,
            ImmutableArray<(string hintName, SourceText sourceText)> sources,
            ImmutableArray<byte> inputChecksum,
            CacheBehavior cacheBehavior)
        {
            foreach (var (hintName, sourceText) in sources)
            {
                context.AddSource(hintName, sourceText);
            }

            switch (cacheBehavior)
            {
                case CacheBehavior.None:
                    break;

                case CacheBehavior.Clear:
                    s_cachedResult.SetTarget(null);
                    break;

                case CacheBehavior.Update:
                    // Overwrite the cached result with the new result. This is an opportunistic cache, so as long as
                    // the write is atomic (which it is for a single pointer) synchronization is unnecessary.
                    s_cachedResult.SetTarget(new CachedSourceGeneratorResult(inputChecksum, sources));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(cacheBehavior));
            }
        }

        private enum CacheBehavior
        {
            None,
            Clear,
            Update,
        }

        private sealed record CachedSourceGeneratorResult(
            ImmutableArray<byte> Checksum,
            ImmutableArray<(string hintName, SourceText sourceText)> Sources);
    }
}

#endif
