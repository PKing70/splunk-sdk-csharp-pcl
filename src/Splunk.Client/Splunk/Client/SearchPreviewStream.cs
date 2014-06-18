﻿/*
 * Copyright 2014 Splunk, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"): you may
 * not use this file except in compliance with the License. You may obtain
 * a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations
 * under the License.
 */

//// TODO:
//// [O] Contracts
//// [O] Documentation

//// References:
//// 1. [Async, await, and yield return](http://goo.gl/RLVDK5)
//// 2. [CLR via C# (4th Edition)](http://goo.gl/SmpI3W)

namespace Splunk.Client
{
    using System;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml;

    /// <summary>
    /// The <see cref="SearchPreviewStream"/> class represents a streaming XML 
    /// reader for Splunk <see cref="SearchResultStream"/>.
    /// </summary>
    public sealed class SearchPreviewStream : Observable<SearchPreview>, IDisposable, IEnumerable<SearchPreview>
    {
        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="SearchPreviewStream"/>
        /// class.
        /// </summary>
        /// <param name="response">
        /// The underlying <see cref="Response"/> object.
        /// </param>
        internal SearchPreviewStream(Response response)
        {
            this.cancellationTokenSource = new CancellationTokenSource();
            this.response = response;

            this.awaiter = new SearchPreviewAwaiter(this, cancellationTokenSource.Token);
        }

        #endregion

        #region Properties

        public Exception LastError
        {
            get { return this.awaiter.LastError; }
        }

        /// <summary>
        /// Gets the <see cref="SearchPreview"/> read count for the current
        /// <see cref="SearchResultStream"/>.
        /// </summary>
        public long ReadCount
        {
            get { return this.awaiter.ReadCount; }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Releases all disposable resources used by the current <see cref=
        /// "SearchPreviewStream"/>.
        /// </summary>
        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            if (this.awaiter.IsReading)
            {
                this.cancellationTokenSource.Cancel();
                this.cancellationTokenSource.Token.WaitHandle.WaitOne();
            }

            this.cancellationTokenSource.Dispose();
            this.response.Dispose();
            this.disposed = true;
        }

        /// <summary>
        /// Returns an enumerator that iterates through <see cref=
        /// "SearchPreview"/> objects on the current <see cref=
        /// "SearchPreviewStream"/> asynchronously.
        /// </summary>
        /// <returns>
        /// An enumerator structure for the <see cref="SearchPreviewStream"/>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// The current <see cref="SearchPreviewStream"/> has already been
        /// enumerated.
        /// </exception>
        /// <remarks>
        /// You can use the <see cref="GetEnumerator"/> method to
        /// <list type="bullet">
        /// <item><description>
        ///   Perform LINQ to Objects queries to obtain a filtered set of 
        ///   search previews.</description></item>
        /// <item><description>
        ///   Append search previews to an existing collection of search
        ///   previews.</description></item>
        /// </list>
        /// </remarks>
        public IEnumerator<SearchPreview> GetEnumerator()
        {
            this.EnsureNotDisposed();

            for (SearchPreview preview; this.awaiter.TryTake(out preview); )
            {
                yield return preview;
            }

            this.EnsureAwaiterSucceeded();
        }

        /// <inheritdoc cref="GetEnumerator"/>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
 
        /// <summary>
        /// Pushes <see cref="SearchPreview"/> instances to subscribers 
        /// and then completes.
        /// </summary>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        protected override async Task PushObservations()
        {
            this.EnsureNotDisposed();

            for (var preview = await this.awaiter; preview != null; preview = await this.awaiter)
            {
                this.OnNext(preview);
            }

            this.EnsureAwaiterSucceeded();
            this.OnCompleted();
        }

        #endregion

        #region Privates/internals

        readonly CancellationTokenSource cancellationTokenSource;
        readonly SearchPreviewAwaiter awaiter;
        readonly Response response;
        bool disposed;

        ReadState ReadState
        {
            get { return this.response.XmlReader.ReadState; }
        }

        void EnsureAwaiterSucceeded()
        {
            if (this.awaiter.LastError != null)
            {
                var text = string.Format("Enumeration ended prematurely : {0}.", this.awaiter.LastError);
                throw new TaskCanceledException(text, this.awaiter.LastError);
            }
        }

        void EnsureNotDisposed()
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException("Search result stream");
            }
        }

        async Task<SearchPreview> ReadPreviewAsync()
        {
            XmlReader reader = this.response.XmlReader;

            reader.Requires(await reader.MoveToDocumentElementAsync("results"));
            var preview = new SearchPreview();
            await preview.ReadXmlAsync(reader);
            await reader.ReadAsync();

            return preview;
        }

        #endregion

        #region Types

        sealed class SearchPreviewAwaiter : INotifyCompletion
        {
            #region Constructors

            public SearchPreviewAwaiter(SearchPreviewStream stream, CancellationToken token)
            {
                this.task = new Task(this.ReadPreviewsAsync, token);
                this.cancellationToken = token;
                this.lastError = null;
                this.stream = stream;
                this.readCount = 0;
                this.task.Start();
            }

            #endregion

            #region Properties

            /// <summary>
            /// 
            /// </summary>
            public bool IsReading
            {
                get { return this.enumerated < 2; }
            }

            /// <summary>
            /// 
            /// </summary>
            public Exception LastError
            {
                get { return this.lastError; }
            }

            /// <summary>
            /// 
            /// </summary>
            public long ReadCount
            {
                get { return Interlocked.Read(ref this.readCount); }
            }

            #endregion

            #region Methods supporting IDisposable and IEnumerable<SearchPreview>

            /// <summary>
            /// 
            /// </summary>
            /// <param name="preview"></param>
            /// <returns></returns>
            public bool TryTake(out SearchPreview preview)
            {
                preview = this.AwaitResultAsync().Result;
                return preview != null;
            }

            #endregion

            #region Members called by the async await state machine

            //// The async await state machine requires that you implement 
            //// INotifyCompletion and provide three additional members: 
            //// 
            ////     IsCompleted property
            ////     GetAwaiter method
            ////     GetResult method
            ////
            //// INotifyCompletion itself defines just one member:
            ////
            ////     OnCompletion method
            ////
            //// See Jeffrey Richter's excellent discussion of the topic of 
            //// awaiters in CLR via C# (4th Edition).

            /// <summary>
            /// Tells the state machine if any results are available.
            /// </summary>
            public bool IsCompleted
            {
                get 
                { 
                    bool result = this.enumerated == 2 || this.previews.Count > 0;
                    return result;
                }
            }

            /// <summary>
            /// Returns the current <see cref="SearchPreviewAwaiter"/> to the
            /// state machine.
            /// </summary>
            /// <returns>
            /// A reference to the current <see cref="SearchPreviewAwaiter"/>.
            /// </returns>
            public SearchPreviewAwaiter GetAwaiter()
            { 
                return this;
            }

            /// <summary>
            /// Returns the current <see cref="SearchResult"/> from the current
            /// <see cref="SearchResultStream"/>.
            /// </summary>
            /// <returns>
            /// The current <see cref="SearchResult"/> or <c>null</c>.
            /// </returns>
            public SearchPreview GetResult()
            {
                SearchPreview preview = null;

                previews.TryDequeue(out preview);
                return preview;
            }

            /// <summary>
            /// Tells the current <see cref="SearchPreviewAwaiter"/> what method
            /// to invoke on completion.
            /// </summary>
            /// <param name="continuation">
            /// The method to call on completion.
            /// </param>
            public void OnCompleted(Action continuation)
            {
                Volatile.Write(ref this.continuation, continuation);
            }

            #endregion

            #region Privates/internals

            ConcurrentQueue<SearchPreview> previews = new ConcurrentQueue<SearchPreview>();
            CancellationToken cancellationToken;
            SearchPreviewStream stream;
            Action continuation;
            Exception lastError;
            int enumerated;
            long readCount;
            Task task;

            async Task<SearchPreview> AwaitResultAsync()
            {
                var result = await this;
                return result;
            }

            void Continue()
            {
                Action continuation = Interlocked.Exchange(ref this.continuation, null);

                if (continuation != null)
                {
                    continuation();
                }

                this.cancellationToken.ThrowIfCancellationRequested();
            }

            async void ReadPreviewsAsync()
            {
                if (Interlocked.CompareExchange(ref this.enumerated, 1, 0) != 0)
                {
                    throw new InvalidOperationException("Stream has been enumerated; The enumeration operation may not execute again.");
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    while (stream.ReadState <= ReadState.Interactive)
                    {
                        var preview = await stream.ReadPreviewAsync();
                        Interlocked.Increment(ref this.readCount);
                        this.previews.Enqueue(preview);
                        this.Continue();
                    }
                }
                catch (Exception error)
                {
                    this.lastError = error;
                }

                this.Continue();
                enumerated = 2;
            }

            #endregion
        }

        #endregion
    }
}