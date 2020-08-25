using System;
using System.Collections.Generic;
using System.Threading;

namespace Dalamud
{
    internal class ThreadExecutor
    {
        private bool end = false;//结束线程标志
        private bool kill = false;//终结线程标志
        private bool stop = false;//暂停线程标志
        private Thread thread = null;//恢复线程标志
        private Queue<RunnableOnThread> msgQueue = null;//存储消息队列
        private bool isClearing = false;
        private object locker = new object();
        private RunnableOnThread insertRunnable;

        public ThreadExecutor()
        {
            msgQueue = new Queue<RunnableOnThread>();
            thread = new Thread(new ThreadStart(Run));//真正定义线程
            thread.Name = "ThreadExecutor" + GetTimeStamp();
        }
        ~ThreadExecutor()
        {
            this.End();//析构时结束线程
        }

        public void RunOnThread(RunnableOnThread runnable)//id为传入的消息标识
        {
            lock (locker)
            {
                if (end || kill)//如果线程结束或终止，不执行任何动作
                    return;
                if (runnable != null)
                    msgQueue.Enqueue(runnable);//将post来的消息添加到消息队列
                if (stop)
                    return;//如果线程暂停，将只接受消息，暂不执行，一旦线程恢复，继续执行所接收消息
                if (!this.thread.IsAlive)//如果线程未开启，将启动线程
                    this.thread.Start();
            }
        }

        public void InsertRunnable(RunnableOnThread runnable)
        {
            insertRunnable = runnable;
        }

        public void Start()
        {
            if (end || kill)//如果线程已被结束或终止，将不执行任何动作
                return;
            if (!this.thread.IsAlive)//如果线程未开启，将启动线程
                thread.Start();
        }
        public void End()
        {
            end = true;//如果线程结束，将结束标识设为真，线程将在消息队列中所有消息执行完后终止
            Console.WriteLine("结束线程");
        }
        public void Kill()
        {
            kill = true;//如果线程终止，将终止标识设为真，线程将不再执行消息队列中剩余消息
            Console.WriteLine("终止线程");
        }
        public void Stop()
        {
            stop = true;//如果线程暂停，将暂停标识设为真，线程将暂不执行消息队列中剩余消息，
                        //但是消息队列仍然在接收消息，一旦线程恢复，继续执行所接收消息
            Console.WriteLine("暂停线程");
        }
        public void Resume()
        {
            stop = false;//如果线程恢复，将恢复标识设为真，线程将继续执行消息队列中剩余消息
            Console.WriteLine("恢复线程");
        }

        public void clearMsg()
        {
            isClearing = true;
            RunOnThread(clearRunable);
        }
        private void Run()
        {
            while (true)
            {
                if (kill)//如果线程终止，线程函数将立即跳出，消息队列里剩余消息不再执行，此线程结束，无法再开启
                    break;
                if (!stop && msgQueue.Count != 0)//如果线程未被暂停且消息队列中有剩余消息，将顺序执行剩余消息
                {
                    if (isClearing)
                    {

                        if ("clearRunable".Equals(msgQueue.Peek().Method.Name))
                        {
                            isClearing = false;
                        }
                        msgQueue.Dequeue();
                    }
                    else
                    {
                        try
                        {
                            insertRunnable?.Invoke();
                            insertRunnable = null;
                            msgQueue.Peek()?.Invoke();
                        }
                        catch { }
                        msgQueue.Dequeue();//比对完当前消息并执行相应动作后，消息队列扔掉当前消息
                    }
                }
                if (msgQueue.Count == 0 && end)//如果线程被结束时当前消息队列中没有消息，将结束此线程
                                               //如果当前消息队列中仍有未执行消息，线程将执行完所有消息后结束
                    break;
                if (!isClearing)
                {
                    System.Threading.Thread.Sleep(100);//每次循环间隔100ms,按键的间隔就会随机在100-0之间
                }
            }
        }

        /*外部传进runnable，运行在子线程中**/
        public delegate void RunnableOnThread();

        private void clearRunable()
        {//
        }

        /// <summary>
        /// 获取时间戳
        /// </summary>
        /// <returns></returns>
        public long GetTimeStamp()
        {
            TimeSpan ts = DateTime.Now - new DateTime(1970, 1, 1, 0, 0, 0, 0);
            return Convert.ToInt64(ts.TotalSeconds);
        }
    }
}
