using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UniRx;

namespace ReactiveSockets
{
    internal delegate TR AsyncFunc<T1, T2, T3, T4, T5, TR>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5);

    internal static class ObservableEx
    {
        public static Func<T1, T2, IObservable<Unit>> FromAsyncPattern<T1, T2>(Func<T1, T2, AsyncCallback, object, IAsyncResult> begin, Action<IAsyncResult> end)
        {
            return (t1, t2) =>
            {
                var future = new AsyncSubject<Unit>();

                begin(t1, t2, ar =>
                {
                    try
                    {
                        end(ar);
                    }
                    catch (Exception ex)
                    {
                        future.OnError(ex);
                        return;
                    }
                    future.OnNext(Unit.Default);
                    future.OnCompleted();
                }, null);

                return future;
            };
        }

        public static Func<IObservable<TR>> FromAsyncPattern<TR>(Func<AsyncCallback, object, IAsyncResult> begin, Func<IAsyncResult, TR> end)
        {
            return () =>
            {
                var future = new AsyncSubject<TR>();

                begin(ar =>
                {
                    TR result;
                    try
                    {
                        result = end(ar);
                    }
                    catch (Exception ex)
                    {
                        future.OnError(ex);
                        return;
                    }
                    future.OnNext(result);
                    future.OnCompleted();
                }, null);

                return future;
            };
        }

        public static Func<T1, IObservable<TR>> FromAsyncPattern<T1, TR>(Func<T1, AsyncCallback, object, IAsyncResult> begin, Func<IAsyncResult, TR> end)
        {
            return (t1) =>
            {
                var future = new AsyncSubject<TR>();

                begin(t1, ar =>
                {
                    TR result;
                    try
                    {
                        result = end(ar);
                    }
                    catch (Exception ex)
                    {
                        future.OnError(ex);
                        return;
                    }
                    future.OnNext(result);
                    future.OnCompleted();
                }, null);

                return future;
            };
        }

        public static Func<T1, T2, T3, IObservable<TR>> FromAsyncPattern<T1, T2, T3, TR>(AsyncFunc<T1, T2, T3, AsyncCallback, object, IAsyncResult> begin, Func<IAsyncResult, TR> end)
        {
            return (t1, t2, t3) =>
            {
                var future = new AsyncSubject<TR>();

                begin(t1, t2, t3, ar =>
                {
                    TR result;
                    try
                    {
                        result = end(ar);
                    }
                    catch (Exception ex)
                    {
                        future.OnError(ex);
                        return;
                    }
                    future.OnNext(result);
                    future.OnCompleted();
                }, null);

                return future;
            };
        }
    }
}