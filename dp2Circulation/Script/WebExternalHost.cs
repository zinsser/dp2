﻿#define SINGLE_CHANNEL
// #define USE_LOCK

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using System.Security.Permissions;
using System.Threading;
using System.Diagnostics;
using System.Xml;
using System.Web;
using System.IO;
using System.Drawing;

using DigitalPlatform;
using DigitalPlatform.Xml;
using DigitalPlatform.Text;
using DigitalPlatform.CommonControl;
using DigitalPlatform.Drawing;
using DigitalPlatform.CirculationClient;
using DigitalPlatform.LibraryClient;

namespace dp2Circulation
{
    /// <summary>
    /// 用于和浏览器控件接口的宿主类
    /// </summary>
    [PermissionSet(SecurityAction.Demand, Name = "FullTrust")]
    [System.Runtime.InteropServices.ComVisibleAttribute(true)]
    public class WebExternalHost : ThreadBase
    {
        public event EventHandler CallFunc = null;

        /// <summary>
        /// 输出调试信息的事件
        /// </summary>
        public event OutputDebugInfoEventHandler OutputDebugInfo = null;

        /// <summary>
        /// 关联的 WebBrowser
        /// </summary>
        public WebBrowser WebBrowser = null;
        /// <summary>
        /// ？？？
        /// </summary>
        public bool DisplayMessage = true; 

        /// <summary>
        /// 获得资源的本地文件路径
        /// </summary>
        public event GetLocalFilePathEventHandler GetLocalPath = null;

        /// <summary>
        /// 浏览器控件是否属于 Hover Window
        /// </summary>
        public bool IsBelongToHoverWindow = false; // 自己是否就属于hover window

        bool m_bLoop = false;

        int m_inSearch = 0;

#if USE_LOCK
        internal ReaderWriterLock m_lock = new ReaderWriterLock();
        internal static int m_nLockTimeout = 5000;	// 5000=5秒
#endif

        /// <summary>
        /// 通讯通道是否正在使用中
        /// </summary>
        public bool ChannelInUse
        {
            get
            {
                if (this.m_inSearch > 0)
                    return true;
                return false;
            }
        }

        /// <summary>
        /// 当前是否正在循环中
        /// </summary>
        public bool IsInLoop
        {
            get
            {
                return this.m_bLoop;
            }
            set
            {
                this.m_bLoop = value;
            }
        }

        /// <summary>
        /// 框架窗口
        /// </summary>
        public MainForm MainForm = null;

        DigitalPlatform.Stop stop = null;

#if SINGLE_CHANNEL
        /// <summary>
        /// 通讯通道
        /// </summary>
        public LibraryChannel Channel = new LibraryChannel();
#else
        public LibraryChannelCollection Channels = null;
#endif

        /// <summary>
        /// 语言代码
        /// </summary>
        public string Lang = "zh";

        /// <summary>
        /// 释放资源
        /// </summary>
        public override void Dispose()
        {
            this.Destroy();

            if (this.Channel != null)
                this.Channel.Dispose();

            base.Dispose();
        }

        /// <summary>
        /// 初始化
        /// </summary>
        /// <param name="mainform">框架窗口</param>
        /// <param name="webBrowser">浏览器控件</param>
        /// <param name="bDisplayMessage">是否显示消息。缺省为 false</param>
        public void Initial(MainForm mainform,
            WebBrowser webBrowser,
            bool bDisplayMessage = false)
        {
            this.MainForm = mainform;
            this.WebBrowser = webBrowser;
#if SINGLE_CHANNEL
            this.Channel.Url = this.MainForm.LibraryServerUrl;

            this.Channel.BeforeLogin -= new BeforeLoginEventHandle(Channel_BeforeLogin);
            this.Channel.BeforeLogin += new BeforeLoginEventHandle(Channel_BeforeLogin);

            this.Channel.Idle -= new IdleEventHandler(Channel_Idle);
            this.Channel.Idle += new IdleEventHandler(Channel_Idle);
#else

            this.Channels = new LibraryChannelCollection();
            this.Channels.BeforeLogin -= new BeforeLoginEventHandle(Channels_BeforeLogin);
            this.Channels.BeforeLogin += new BeforeLoginEventHandle(Channels_BeforeLogin);
#endif

            this.DisplayMessage = bDisplayMessage;

            if (bDisplayMessage == true)
            {
                stop = new DigitalPlatform.Stop();
                stop.Register(MainForm.stopManager, true);	// 和容器关联
            }

            // this.BeginThread();
        }

        void Channel_Idle(object sender, IdleEventArgs e)
        {
            // e.bDoEvents = this._doEvents;

            // 2016/1/26
            if (this._doEvents)
                Application.DoEvents();
        }

        /// <summary>
        /// 摧毁本对象
        /// </summary>
        public void Destroy()
        {

#if SINGLE_CHANNEL

            // 2008/5/11 
            if (this.Channel != null)
            {
                this.Channel.Idle -= new IdleEventHandler(Channel_Idle);
                this.Channel.BeforeLogin -= new BeforeLoginEventHandle(Channel_BeforeLogin);
                this.IsInLoop = false;  // 2008/10/29 
                this.Channel.Close();   // 2012/3/28
                this.Channel = null;
            }
#else
            CloseAllChannels();
            this.Channels = null;
#endif
            if (stop != null) // 脱离关联
            {
                stop.Unregister();	// 和容器关联
                stop = null;
            }

            this.Clear();
            this.StopThread(false);
        }

        // 最近一次使用 AbortIt() 的时间
        DateTime _lastAbortTime = new DateTime(0);

        /// <summary>
        /// 停止通讯
        /// </summary>
        public void Stop()
        {
            // this.IsInLoop = false;  // 2008/10/29 

            this.Clear();

#if SINGLE_CHANNEL

            // 2008/5/11 
            if (this.Channel != null)
            {
                if (this.Channel.IsInSearching > 0)
                {
                    DateTime now = DateTime.Now;
                    // 每隔 5 分钟才允许使用一次 AbortIt()
                    if (this._lastAbortTime - now > new TimeSpan(0,5,0))
                    {
                        this.Channel.AbortIt();
                        this._lastAbortTime = now;
                    }
                    else
                        this.Channel.Abort();
                    // this.Channel.AbortIt(); // 能立即切断通讯。但会留下很多丢弃的通道，很快会突破服务器端对每个 IP 50 个通道的限制
                }
            }
#else
            CloseAllChannels();
            this.Channels = null;
#endif
        }

        /// <summary>
        /// 当前是否可以调用新的命令了
        /// </summary>
        /// <param name="commander">Commander对象</param>
        /// <param name="msg">消息</param>
        /// <returns>是否可以调用新的命令</returns>
        public bool CanCallNew(Commander commander,
            int msg)
        {
            if (this.IsInLoop == true)
            {
                // 缓兵之计
                this.IsInLoop = false;
                commander.AddMessage(msg);
                return false;   // 还不能启动
            }

            Debug.Assert(this.IsInLoop == false, "启动前发现上一次循环尚未停止");

            if (this.ChannelInUse == true)
            {
                // 缓兵之计
                this.Stop();
                commander.AddMessage(msg);
                return false;   // 还不能启动
            }

            Debug.Assert(this.ChannelInUse == false, "启动前发现通道还未释放");
            return true;    // 可以启动
        }

        /// <summary>
        /// 停止前一个命令
        /// </summary>
        public void StopPrevious()
        {
            this.IsInLoop = false;
            this.Stop();
        }

        void DoStop(object sender, StopEventArgs e)
        {
#if SINGLE_CHANNEL
            if (this.Channel != null)
                this.Channel.Abort();
#else
            CloseAllChannels();
#endif
        }

#if !SINGLE_CHANNEL

        void CloseAllChannels()
        {
            if (this.Channels == null)
                return;

            for (int i = 0; i < this.Channels.Count; i++)
            {
                LibraryChannel channel = this.Channels[i];

                channel.Abort();
            }
        }

        void ReleaseAllChannelsBut(string strID)
        {
            for (int i = 0; i < this.Channels.Count; i++)
            {
                LibraryChannel channel = this.Channels[i];

                string strCurrentID = (string)channel.Tag;
                if (strCurrentID == strID)
                    continue;

                channel.Abort();
                this.Channels.RemoveChannel(channel);
                i--;
            }

            // TODO: 如何对通道进行回收利用？是否设立一个时间差额的成员，在一定时间后重用?
        }

        LibraryChannel GetChannelByID(string strID)
        {
            if (this.Channels == null)
                return null;
            LibraryChannel channel = null;
            for (int i = 0; i < this.Channels.Count; i++)
            {
                channel = this.Channels[i];
                string strCurrentID = (string)channel.Tag;
                if (strCurrentID == strID)
                    return channel;
            }
            channel = this.Channels.NewChannel(MainForm.LibraryServerUrl);
            channel.Tag = strID;
            return channel;
           
        }
#endif

#if SINGLE_CHANNEL

        void Channel_BeforeLogin(object sender, BeforeLoginEventArgs e)
        {
            MainForm.Channel_BeforeLogin(sender, e);    // 2015/11/8
        }
#else
        void Channels_BeforeLogin(object sender, BeforeLoginEventArgs e)
        {
            MainForm.Channel_BeforeLogin(sender, e);    // 2015/11/8
        }
#endif

        /// <summary>
        /// 开始循环
        /// </summary>
        public void BeginLoop()
        {
            this.m_bLoop = true;
        }

        /// <summary>
        /// 结束循环
        /// </summary>
        public void EndLoop()
        {
            this.m_bLoop = false;
        }

        int m_nInHoverProperty = 0;

        /// <summary>
        /// 显示册属性
        /// </summary>
        /// <param name="strItemBarcode">册条码号</param>
        public void HoverItemProperty(string strItemBarcode)
        {
            this.m_nInHoverProperty++;
            if (this.m_nInHoverProperty > 1)
            {
                this.m_nInHoverProperty--;
                return;
            }

            try
            {
                if (this.IsBelongToHoverWindow == true)
                    return;

                if (this.MainForm.CanDisplayItemProperty() == false)
                    return;

                if (this.MainForm.GetItemPropertyTitle() == strItemBarcode)
                    return; // 优化

                if (string.IsNullOrEmpty(strItemBarcode) == true)
                {
                    this.MainForm.DisplayItemProperty("",
        "",
        "");
                    return;
                }

                string strError = "";

                if (stop != null)
                {
                    stop.OnStop += new StopEventHandler(this.DoStop);
                    stop.SetMessage("正在获取册信息 '" + strItemBarcode + "' ...");
                    stop.BeginLoop();
                }

                try
                {
                    // Application.DoEvents();

                    this.m_inSearch++;

#if SINGLE_CHANNEL
                    // 因为本对象只有一个Channel通道，所以要锁定使用
                    if (this.m_inSearch > 1)
                    {
                        /*
                        strError = "Channel被占用";
                        goto ERROR1;
                         * */
                        this.m_inSearch--;
                        return;
                    }
                    //// LibraryChannel channel = this.Channel;
#else
                LibraryChannel channel = GetChannelByID(strIdString);
#endif

                    try
                    {
                        string strItemText = "";
                        string strBiblioText = "";

                        string strItemRecPath = "";
                        string strBiblioRecPath = "";

                        byte[] item_timestamp = null;

                        this.Channel.Timeout = new TimeSpan(0, 0, 5);
                        long lRet = this.Channel.GetItemInfo(
                            stop,
                            strItemBarcode,
                            "html",
                            out strItemText,
                            out strItemRecPath,
                            out item_timestamp,
                            "",
                            out strBiblioText,
                            out strBiblioRecPath,
                            out strError);
                        if (lRet == 0)
                        {
                            strError = "册条码号 '" + strItemBarcode + "' 没有找到";
                            goto ERROR1;
                        }
                        if (lRet == -1)
                            goto ERROR1;

                        string strXml = "";
                        this.Channel.Timeout = new TimeSpan(0, 0, 5);
                        lRet = this.Channel.GetItemInfo(
        stop,
        strItemBarcode,
        "xml",
        out strXml,
        out strItemRecPath,
        out item_timestamp,
        "",
        out strBiblioText,
        out strBiblioRecPath,
        out strError);

                        this.MainForm.DisplayItemProperty(strItemBarcode,
                            strItemText,
                            strXml);

                        return;
                    }
                    catch
                    {
                        // return "GetObjectFilePath()异常: " + ex.Message;
                        throw;
                    }
                    finally
                    {
                        this.m_inSearch--;
                    }

                }
                finally
                {
                    if (stop != null)
                    {
                        stop.EndLoop();
                        stop.OnStop -= new StopEventHandler(this.DoStop);
                        stop.Initial("");
                    }
                }

            ERROR1:

                this.MainForm.DisplayItemProperty("error",
                    strError,
                    "");
            }
            finally
            {
                this.m_nInHoverProperty--;
            }
        }

        // 
        /// <summary>
        /// 打开 MDI 子窗口
        /// </summary>
        /// <param name="strFormName">窗口名称。ItemInfoForm / EntityForm / ReaderInfoForm</param>
        /// <param name="strParameter">参数字符串</param>
        /// <param name="bOpenNew">是否打开新的窗口</param>
        public void OpenForm(string strFormName, 
            string strParameter,
            bool bOpenNew)
        {
            if (strFormName == "ItemInfoForm")
            {
                ItemInfoForm form = null;
                if (bOpenNew == false)
                {
                    form = this.MainForm.EnsureItemInfoForm();
                    Global.Activate(form);
                }
                else
                {
                    form = new ItemInfoForm();
                    form.MainForm = this.MainForm;
                    form.MdiParent = this.MainForm;
                    form.Show();
                }
                form.LoadRecord(strParameter);  // 用册条码号装载
                return;
            }

            if (strFormName == "EntityForm")
            {
                EntityForm form = null;
                if (bOpenNew == false)
                {
                    form = this.MainForm.EnsureEntityForm();
                    Global.Activate(form);
                }
                else
                {
                    form = new EntityForm();
                    form.MainForm = this.MainForm;
                    form.MdiParent = this.MainForm;
                    form.Show();
                }
                form.LoadItemByBarcode(strParameter, false);  // 用册条码号装载
                return;
            }

            if (strFormName == "ReaderInfoForm")
            {
                ReaderInfoForm form = null;
                if (bOpenNew == false)
                {
                    form = this.MainForm.EnsureReaderInfoForm();
                    Global.Activate(form);
                }
                else
                {
                    form = new ReaderInfoForm();
                    form.MainForm = this.MainForm;
                    form.MdiParent = this.MainForm;
                    form.Show();
                }
                form.LoadRecord(strParameter,
                    false); 
                return;
            }
        }

        public void AsyncGetObjectFilePath(string strPatronBarcode,
            string strUsage,
            string strCallBackFuncName,
            object element)
        {
            // this.WebBrowser.Document.InvokeScript(strCallBackFuncName, new object[] { "state", o, "result" });
            AsyncCall call = new AsyncCall();
            call.FuncType = "AsyncGetObjectFilePath";
            call.InputParameters = new object[] { strPatronBarcode, strUsage, strCallBackFuncName, element};
            this.AddCall(call);
        }

        List<string> _tempfilenames = new List<string>();

        string GetTempFileName()
        {
            string strTempFilePath = Path.Combine(this.MainForm.UserTempDir, "~res_" + Guid.NewGuid().ToString());

            lock (this._tempfilenames)
            {
                _tempfilenames.Add(strTempFilePath);
            }

            return strTempFilePath;
        }

        // 2015/1/4
        void DeleteAllTempFiles()
        {
            foreach (string filename in this._tempfilenames)
            {
                try
                {
                    File.Delete(filename);
                }
                catch
                {
                }
            }

            lock (this._tempfilenames)
            {
                this._tempfilenames.Clear();
            }
        }

        //
        // TODO: 获得读者记录XML时，尽量用Cache。Cache的读者记录中<dprms:file>一般不会有变动
        /// <summary>
        ///  获得对象本地文件路径
        /// </summary>
        /// <param name="strPatronBarcode">读者证条码号，或者命令字符串</param>
        /// <param name="strUsage">用途</param>
        /// <returns>本地文件路径</returns>
        public string GetObjectFilePath(string strPatronBarcode,
            string strUsage)
        {

            // this.WebBrowser.Document.InvokeScript("test", new object [] {"test1", "test2"});

            /*
            if (this.IsInLoop == false)
                throw new Exception("已经不在循环中");
             * */
            long lRet = 0;

            string strNoneFilePath = Path.Combine(this.MainForm.UserDir, "nonephoto.png");

            // 2012/1/6
            if (string.IsNullOrEmpty(strPatronBarcode) == true)
                return strNoneFilePath;

            // 获得本地图像资源
            if (strPatronBarcode == "?")
            {
                // return this.MainForm.DataDir + "/~current_unsaved_patron_photo.png";
                if (this.GetLocalPath != null)
                {
                    GetLocalFilePathEventArgs e = new GetLocalFilePathEventArgs();
                    e.Name = "PatronCardPhoto";
                    this.GetLocalPath(this, e);
                    if (e.LocalFilePath == null)
                        return strNoneFilePath;
                    if (string.IsNullOrEmpty(e.LocalFilePath) == false)
                        return e.LocalFilePath;
                }
            }

            if (this.DisplayMessage == true && stop == null)
            {
                return null;
            }

#if !SINGLE_CHANNEL

            if (this.Channels == null)
            {
                return "channels closed 2...";
            }
            ReleaseAllChannelsBut(strIdString);
#endif

            string strError = "";

#if USE_LOCK
            this.m_lock.AcquireWriterLock(m_nLockTimeout);
            try
            {
#endif
            if (stop != null)
            {
                stop.OnStop += new StopEventHandler(this.DoStop);
                stop.SetMessage("正在获取读者照片 '" + strPatronBarcode + "' ...");
                stop.BeginLoop();
            }

            try
            {
                // Application.DoEvents();

#if SINGLE_CHANNEL
                // 因为本对象只有一个Channel通道，所以要锁定使用
                if (this.m_inSearch > 0)
                {
                    return "Channel被占用";
                }
                //// LibraryChannel channel = this.Channel;
#else
                LibraryChannel channel = GetChannelByID(strIdString);
#endif

                this.m_inSearch++;
                try
                {
                    string strResPath = "";
                    if (StringUtil.HasHead(strPatronBarcode, "object-path:") == true)
                    {
                        // 可以直接获得图像对象
                        strResPath = strPatronBarcode.Substring("object-path:".Length);
                        if (string.IsNullOrEmpty(strResPath) == true)
                            return strNoneFilePath;
                    }
                    else
                    {
                        // 需要先取得读者记录然后再获得图像对象
                        string strXml = "";
                        string strOutputPath = "";

                        // 获得缓存中的读者记录XML
                        // return:
                        //      -1  出错
                        //      0   没有找到
                        //      1   找到
                        int nRet = this.MainForm.GetCachedReaderXml(strPatronBarcode,
                            "",
        out strXml,
        out strOutputPath,
        out strError);
                        if (nRet == -1)
                        {
                            throw new Exception(strError);
                            // return strError;
                        }

                        if (nRet == 0)
                        {

                            string[] results = null;
                            byte[] baTimestamp = null;

                            this.Channel.Timeout = new TimeSpan(0, 0, 5);
                            lRet = Channel.GetReaderInfo(stop,
                                strPatronBarcode,
                                "xml",
                                out results,
                                out strOutputPath,
                                out baTimestamp,
                                out strError);
                            if (lRet == -1)
                            {
                                throw new Exception(strError);
                                // return strError;
                            }
                            else if (lRet > 1)
                            {
                                strError = "读者证条码号 " + strPatronBarcode + " 有重复记录 " + lRet.ToString() + "条";
                                throw new Exception(strError);
                                // return strError;
                            }

                            Debug.Assert(results.Length > 0, "");
                            strXml = results[0];

                            // 加入到缓存
                            this.MainForm.SetReaderXmlCache(strPatronBarcode,
                                "",
                                strXml,
                                strOutputPath);
                        }

                        XmlDocument dom = new XmlDocument();
                        try
                        {
                            dom.LoadXml(strXml);
                        }
                        catch (Exception ex)
                        {
                            strError = "读者记录XML装入DOM时出错: " + ex.Message;
                            throw new Exception(strError);
                            // return strError;
                        }


                        XmlNamespaceManager nsmgr = new XmlNamespaceManager(new NameTable());
                        nsmgr.AddNamespace("dprms", DpNs.dprms);

                        XmlNodeList nodes = null;
                        if (string.IsNullOrEmpty(strUsage) == false)
                            nodes = dom.DocumentElement.SelectNodes("//dprms:file[@usage='" + strUsage + "']", nsmgr);
                        else
                            nodes = dom.DocumentElement.SelectNodes("//dprms:file", nsmgr);

                        if (nodes.Count == 0)
                        {
                            return strNoneFilePath;
                        }

                        string strID = DomUtil.GetAttr(nodes[0], "id");
                        if (string.IsNullOrEmpty(strID) == true)
                            return null;

                        strResPath = strOutputPath + "/object/" + strID;
                        strResPath = strResPath.Replace(":", "/");

                    }

                    // string strTempFilePath = this.MainForm.DataDir + "/~temp_obj";

                    string strTempFilePath = GetTempFileName();
                    // TODO: 是否可以建立本地 cache 机制

                    byte[] baOutputTimeStamp = null;

                    // EnableControlsInLoading(true);

                    string strMetaData = "";
                    string strTempOutputPath = "";

                    this.Channel.Timeout = new TimeSpan(0, 0, 60);
                    lRet = this.Channel.GetRes(
                        stop,
                        strResPath,
                        strTempFilePath,
                        out strMetaData,
                        out baOutputTimeStamp,
                        out strTempOutputPath,
                        out strError);
                    if (lRet == -1)
                    {
                        strError = "下载资源文件失败，原因: " + strError;
                        throw new Exception(strError);
                        // return strError;
                    }

                    return strTempFilePath;
                }
                catch/*(Exception ex)*/
                {
                    // return "GetObjectFilePath()异常: " + ex.Message;
                    throw;
                }
                finally
                {
                    this.m_inSearch--;
                }
            }
            finally
            {
                if (stop != null)
                {
                    stop.EndLoop();
                    stop.OnStop -= new StopEventHandler(this.DoStop);
                    stop.Initial("");
                }
            }

#if USE_LOCK
            }
            finally
            {
                this.m_lock.ReleaseWriterLock();
            }
#endif
        }

        public void AsyncGetPatronSummary(string strPatronBarcode,
            string strCallBackFuncName,
            object element)
        {
            AsyncCall call = new AsyncCall();
            call.FuncType = "AsyncGetPatronSummary";
            call.InputParameters = new object[] { strPatronBarcode, strCallBackFuncName, element };
            this.AddCall(call);
        }

        // 
        /// <summary>
        /// 获得读者摘要
        /// </summary>
        /// <param name="strPatronBarcode">读者证条码号，或者命令字符串</param>
        /// <returns>读者摘要</returns>
        public string GetPatronSummary(string strPatronBarcode)
        {
            if (this.IsInLoop == false)
                throw new Exception("已经不在循环中");

            if (this.DisplayMessage == true && stop == null)
            {
                return "channels closed 1...";
            }

#if !SINGLE_CHANNEL

            if (this.Channels == null)
            {
                return "channels closed 2...";
            }
            ReleaseAllChannelsBut(strIdString);
#endif

            string strError = "";
            string strSummary = "";

            int nRet = strPatronBarcode.IndexOf("|");
            if (nRet != -1)
                return "证条码号字符串 '"+strPatronBarcode+"' 中不应该有竖线字符";


            // 看看cache中是否已经有了
            StringCacheItem item = null;
            item = this.MainForm.SummaryCache.SearchItem(
                "P:" + strPatronBarcode);   // 前缀是为了和册条码号区别
            if (item != null)
            {
                // Application.DoEvents();
                strSummary = item.Content;
                return strSummary;
            }

            /*
            int nRet = strItemBarcodeUnionPath.IndexOf("|");
            if (nRet == -1)
            {
                strItemBarcode = strItemBarcodeUnionPath.Trim();
            }
            else
            {
                strItemBarcode = strItemBarcodeUnionPath.Substring(0, nRet).Trim();
                strConfirmReaderRecPath = strItemBarcodeUnionPath.Substring(nRet + 1).Trim();

                nRet = strConfirmReaderRecPath.IndexOf("||");
                if (nRet != -1)
                    strConfirmReaderRecPath = strConfirmReaderRecPath.Substring(0, nRet);
            }*/

#if USE_LOCK
            this.m_lock.AcquireWriterLock(m_nLockTimeout);
            try
            {
#endif
            if (stop != null)
            {
                stop.OnStop += new StopEventHandler(this.DoStop);
                stop.SetMessage("正在获取读者摘要 '" + strPatronBarcode + "' ...");
                stop.BeginLoop();
            }

                try
                {
                    // Application.DoEvents();

#if SINGLE_CHANNEL
                    // 因为本对象只有一个Channel通道，所以要锁定使用
                    if (this.m_inSearch > 0)
                    {
                        return "Channel被占用";
                    }
                    //// LibraryChannel channel = this.Channel;
#else
                LibraryChannel channel = GetChannelByID(strIdString);
#endif

                    this.m_inSearch++;
                    try
                    {
                        string strXml = "";
                        string[] results = null;
                        this.Channel.Timeout = new TimeSpan(0, 0, 5);
                        long lRet = Channel.GetReaderInfo(stop,
                            strPatronBarcode,
                            "xml",
                            out results,
                            out strError);
                        if (lRet == -1)
                        {
                            strSummary = strError;
                            return strSummary;
                        }
                        else if (lRet > 1)
                        {
                            strSummary = "读者证条码号 " + strPatronBarcode + " 有重复记录 " + lRet.ToString() + "条";
                            return strSummary;
                        }

                        // 2012/10/1
                        if (lRet == 0)
                            return "";  // not found

                        Debug.Assert(results.Length > 0, "");
                        strXml = results[0];

                        XmlDocument dom = new XmlDocument();
                        try
                        {
                            dom.LoadXml(strXml);
                        }
                        catch (Exception ex)
                        {
                            strSummary = "读者记录XML装入DOM时出错: " + ex.Message;
                            return strSummary;
                        }

                        // 读者姓名
                        strSummary = DomUtil.GetElementText(dom.DocumentElement,
                            "name");
                    }
                    catch (Exception ex)
                    {
                        return "GetPatronSummary()异常: " + ex.Message;
                    }
                    finally
                    {
                        this.m_inSearch--;
                    }

                    // 如果cache中没有，则加入cache
                    item = this.MainForm.SummaryCache.EnsureItem(
                        "P:" + strPatronBarcode);
                    item.Content = strSummary;
                }
                finally
                {
                    if (stop != null)
                    {
                        stop.EndLoop();
                        stop.OnStop -= new StopEventHandler(this.DoStop);
                        stop.Initial("");
                    }
                }
                return strSummary;
#if USE_LOCK
            }
            finally
            {
                this.m_lock.ReleaseWriterLock();
            }
#endif
        }

        public void AsyncGetSummary(String strItemBarcodeUnionPath,
            bool bCutting,
            string strCallBackFuncName,
            object element)
        {
            AsyncCall call = new AsyncCall();
            call.FuncType = "AsyncGetSummary";
            call.InputParameters = new object[] { strItemBarcodeUnionPath, bCutting, strCallBackFuncName, element };
            this.AddCall(call);
        }

        // 以前的版本：获得书目摘要
        // TODO: 是否可以被有意中断?
        /// <summary>
        /// 获得书目摘要
        /// </summary>
        /// <param name="strItemBarcodeUnionPath">册条码号，或者命令字符串。xxxxx  B:xxxxx  BC:xxxxx 包含封面</param>
        /// <param name="bCutting">是否截断过长的结果字符串</param>
        /// <returns>书目摘要</returns>
        public string GetSummary(String strItemBarcodeUnionPath,
            bool bCutting = true)
        {
            if (string.IsNullOrEmpty(strItemBarcodeUnionPath) == true)
                return "strItemBarcodeUnionPath为空";

            if (this.IsInLoop == false)
                throw new Exception("已经不在循环中");
            // Debug.WriteLine("id=" + strIdString);

            if (this.DisplayMessage == true && stop == null)
            {
                return "channels closed 1...";
            }


#if !SINGLE_CHANNEL

            if (this.Channels == null)
            {
                return "channels closed 2...";
            }
            ReleaseAllChannelsBut(strIdString);
#endif


            // MessageBox.Show(message, "client code");
            string strConfirmItemRecPath = "";
            string strError = "";
            string strSummary = "";
            string strBiblioRecPath = "";
            string strItemBarcode = "";

            // test Thread.Sleep(1000);

            // 看看cache中是否已经有了
            StringCacheItem item = null;

            item = this.MainForm.SummaryCache.SearchItem(strItemBarcodeUnionPath);
            if (item != null)
            {
                // Application.DoEvents();
                strSummary = item.Content;
                goto END1;  // 还要截断
            }

            int nRet = strItemBarcodeUnionPath.IndexOf("|");
            if (nRet == -1)
            {
                strItemBarcode = strItemBarcodeUnionPath.Trim();
            }
            else
            {
                strItemBarcode = strItemBarcodeUnionPath.Substring(0, nRet).Trim();
                strConfirmItemRecPath = strItemBarcodeUnionPath.Substring(nRet + 1).Trim();

                nRet = strConfirmItemRecPath.IndexOf("||");
                if (nRet != -1)
                    strConfirmItemRecPath = strConfirmItemRecPath.Substring(0, nRet);
            }

            // 处理 B:xxxxxx 形态 2014/12/27 
            bool bContainCover = false;
            {
                string strPrefix = "";
                string strContent = "";
                StringUtil.ParseTwoPart(strItemBarcode, ":", out strPrefix, out strContent);
                if (string.IsNullOrEmpty(strContent) == true)
                    strContent = strPrefix;
                else
                {
                    if (strPrefix.ToUpper() == "BC")
                        bContainCover = true;
                }
                strItemBarcode = strContent;
            }

#if USE_LOCK
            this.m_lock.AcquireWriterLock(m_nLockTimeout);
            try
            {
#endif
            if (stop != null)
            {
                stop.OnStop += new StopEventHandler(this.DoStop);
                stop.SetMessage("正在获取书目摘要 '" + strItemBarcode + "' ...");
                stop.BeginLoop();
            }

            try
            {
                // Application.DoEvents();

#if SINGLE_CHANNEL
                // 因为本对象只有一个Channel通道，所以要锁定使用
                if (this.m_inSearch > 0)
                {
                    return "Channel被占用";
                }
                //// LibraryChannel channel = this.Channel;
#else
                LibraryChannel channel = GetChannelByID(strIdString);
#endif

                this.m_inSearch++;
                try
                {
                    // TODO: 这里可以累计失败的次数，作为通讯状况是否良好的一个指标。
                    // 通讯状态或可在界面上有所显示
                    long lRet = 0;
                    // 最多重试 n 次
                    for (int i = 0; i < 1; i++)
                    {
                        if (stop != null && stop.State != 0)
                        {
                            lRet = -1;
                            strError = "中断";
                            break;
                        }

                        if (strItemBarcode.StartsWith("_testitem") == true)
                        {
                            lRet = 1;
                            strBiblioRecPath = "_测试书目库/1";
                            strSummary = "测试书名/测试作者. -- ISBN 978-7-5397-3818-5";
                            break;
                        }

                        // 注: Channel.Timeout 在 GetBiblioSummary() 函数中会自动设置
                        
                        lRet = this.Channel.GetBiblioSummary(
                            stop,
                            strItemBarcode,
                            strConfirmItemRecPath,
                            bContainCover == false ? null : "coverimage",
                            out strBiblioRecPath,
                            out strSummary,
                            out strError);
                        if (lRet == -1)
                            continue;
                    }
                    if (lRet == -1)
                        return strError;
                }
                catch (Exception ex)
                {
                    return "GetBiblioSummary()异常: " + ex.Message;
                }
                finally
                {
                    this.m_inSearch--;
                }

                // 如果cache中没有，则加入cache
                item = this.MainForm.SummaryCache.EnsureItem(strItemBarcodeUnionPath);
                item.Content = strSummary;
            }
            finally
            {
                if (stop != null)
                {
                    stop.EndLoop();
                    stop.OnStop -= new StopEventHandler(this.DoStop);
                    stop.Initial("");
                }
            }
#if USE_LOCK
            }
            finally
            {
                this.m_lock.ReleaseWriterLock();
            }
#endif

        END1:
            if (bCutting == true)
            {
                if (string.IsNullOrEmpty(strSummary) == false && strSummary[0] == '<')
                {
                    string strXml = "<root>" + strSummary + "</root>";
                    XmlDocument temp_dom = new XmlDocument();
                    try
                    {
                        temp_dom.LoadXml(strXml);
                    }
                    catch
                    {
                        goto END2;
                    }

                    XmlNode text = null;
                    foreach (XmlNode node in temp_dom.DocumentElement.ChildNodes)
                    {
                        if (node.NodeType == XmlNodeType.Text)
                            text = node;
                    }
                    if (text != null)
                    {
                        string strInnerText = text.Value;
                        // 截断
                        if (strInnerText.Length > 25)
                            strInnerText = strInnerText.Substring(0, 25) + "...";

                        if (strInnerText.Length > 12)
                        {
                            text.Value = strInnerText.Substring(0, 12);
                            XmlNode br = text.ParentNode.InsertAfter(temp_dom.CreateElement("br"), text);

                            XmlNode new_text = temp_dom.CreateTextNode(strInnerText.Substring(12));
                            text.ParentNode.InsertAfter(new_text, br);
                        }
                        else
                            text.Value = strInnerText;
                    }

                    strSummary = temp_dom.DocumentElement.InnerXml;
                }
                else
                {
                    // 截断
                    if (strSummary.Length > 25)
                        strSummary = strSummary.Substring(0, 25) + "...";

                    if (strSummary.Length > 12)
                        strSummary = strSummary.Insert(12, "<br/>");
                }
            }
            END2:
            return strSummary;
        }

        #region Thread support

        bool _doEvents = true;
        internal ReaderWriterLock m_lock = new ReaderWriterLock();
        internal static int m_nLockTimeout = 5000;	// 5000=5秒

        List<AsyncCall> m_calls = new List<AsyncCall>();

        void AddCall(AsyncCall call)
        {
            // TODO: 这里可能会抛出异常
            this.m_lock.AcquireWriterLock(m_nLockTimeout);
            try
            {
                this.m_calls.Add(call);
                //Debug.WriteLine("AddCall " + call.FuncType);
            }
            finally
            {
                this.m_lock.ReleaseWriterLock();
            }

            if (this._thread == null)
                this.BeginThread();
            else
                Activate();
        }

        public void Clear()
        {
            this.m_lock.AcquireWriterLock(m_nLockTimeout);
            try
            {
                this.m_calls.Clear();
                //Debug.WriteLine("Clear All Calls 1");

                this.DeleteAllTempFiles();  // 2015/1/4
            }
            finally
            {
                this.m_lock.ReleaseWriterLock();
            }

            //    _doingCalls.Clear();
            _stopDoingCalls = true;
        }

        bool _stopDoingCalls = false;

        // 工作线程每一轮循环的实质性工作
        public override void Worker()
        {
            _stopDoingCalls = false;
            List<AsyncCall> doingCalls = new List<AsyncCall>();

            this.m_lock.AcquireWriterLock(m_nLockTimeout);
            try
            {
                for (int i = 0; i < this.m_calls.Count; i++)
                {
                    if (this.Stopped == true)
                        return;
                    if (this.IsInLoop == false)
                        return;

                    AsyncCall call = this.m_calls[i];

                    doingCalls.Add(call);
                }

                this.m_calls.Clear();
                //Debug.WriteLine("Clear All Calls 2");
            }
            finally
            {
                this.m_lock.ReleaseWriterLock();
            }

            try
            {
                foreach (AsyncCall call in doingCalls)
                {
                    if (_stopDoingCalls == true)
                        return;

                    if (this.Stopped == true)
                        return;
                    if (this.IsInLoop == false)
                        return;
                    
                    // AsyncCall call = doingCalls[i];

                    this._doEvents = false; // 只要调用过一次异步功能，从此就不出让控制权

                    //Debug.WriteLine("Call " + call.FuncType);
                    if (call.FuncType == "AsyncGetObjectFilePath")
                    {
                        string strResult = "";
                        try
                        {
                            // 此调用可能耗费时间好几秒
                            strResult = GetObjectFilePath((string)call.InputParameters[0], (string)call.InputParameters[1]);
                        }
                        catch (Exception ex)
                        {
                            strResult = ex.Message;
                            // 2014/9/9
                            DoOutputDebugInfo("AsyncGetObjectFilePath 异常：" + ExceptionUtil.GetDebugText(ex));
                            // goto CONTINUE;
                            continue;
                        }

                        // this.WebBrowser.Document.InvokeScript((string)call.InputParameters[2], new object[] { (object)call.InputParameters[3], (object)strResult });
                        this.MainForm.BeginInvokeScript(this.WebBrowser,
                            (string)call.InputParameters[2],
                            new object[] { (object)call.InputParameters[3], (object)strResult });
                    }
                    if (call.FuncType == "AsyncGetSummary")
                    {
                        string strResult = GetSummary((string)call.InputParameters[0], (bool)call.InputParameters[1]);
                        // this.WebBrowser.Document.InvokeScript((string)call.InputParameters[2], new object[] { (object)call.InputParameters[3], (object)strResult });
                        this.MainForm.BeginInvokeScript(this.WebBrowser,
                            (string)call.InputParameters[2],
                            new object[] { (object)call.InputParameters[3], (object)strResult });
                    }
                    if (call.FuncType == "AsyncGetPatronSummary")
                    {
                        string strResult = GetPatronSummary((string)call.InputParameters[0]);
                        // this.WebBrowser.Document.InvokeScript((string)call.InputParameters[2], new object[] { (object)call.InputParameters[3], (object)strResult });
                        this.MainForm.BeginInvokeScript(this.WebBrowser,
                            (string)call.InputParameters[1],
                            new object[] { (object)call.InputParameters[2], (object)strResult });
                    }

#if NO
                CONTINUE:
                    doingCalls.RemoveAt(0);
                    i--;
#endif
                }
                //     _doingCalls.Clear();
            }
            catch (Exception ex)
            {
                /*
                if (this.m_bStopThread == false)
                    throw;
                 * */
                // 2014/9/9
                // 要在一个控制台输出这些异常信息，帮助诊断
                string strError = "WebExternalHost 异常：" + ExceptionUtil.GetDebugText(ex);
                DoOutputDebugInfo(strError);
                this.MainForm.ReportError("dp2circulation WebExternalHost 异常",
                    strError);
            }
        }

        void DoOutputDebugInfo(string strText)
        {
            if (this.OutputDebugInfo != null)
            {
                OutputDebugInfoEventArgs e = new OutputDebugInfoEventArgs();
                e.Text = strText;
                this.OutputDebugInfo(this, e);
            }
        }

        #endregion

        /// <summary>
        /// 设置 HTML 页内容
        /// 自动停止先前的异步处理
        /// </summary>
        /// <param name="strHtml">HTML 字符串</param>
        /// <param name="strTempFileType">临时文件前缀</param>
        public void SetHtmlString(string strHtml,
            string strTempFileType)
        {
            this.StopPrevious();
            this.WebBrowser.Stop();

            this.DeleteAllTempFiles();  // 2015/1/4

            Global.SetHtmlString(this.WebBrowser,
                strHtml,
                this.MainForm.DataDir,
                strTempFileType);
        }

        /// <summary>
        /// 设置 文本 页内容
        /// 自动停止先前的异步处理
        /// </summary>
        /// <param name="strText">HTML 字符串</param>
        /// <param name="strTempFileType">临时文件前缀</param>
        public void SetTextString(string strText,
            string strTempFileType = "")
        {
            this.StopPrevious();
            this.WebBrowser.Stop();

            if (string.IsNullOrEmpty(strTempFileType) == true)
                strTempFileType = "temp_text";

            string body_backcolor = "#999999";
            string div_backcolor = "#ffff99";

            body_backcolor = ColorUtil.Color2String(this.BackColor);

            // TODO: 大字居中显示
            string strHtml = @"<html>
<head>
<style type='text/css'>
body {
background-color: "+body_backcolor+@";
}
div {
background-color: "+div_backcolor+@";
margin: 32px;
padding: 32px;
border-style: solid;
border-width: 1px;
border-color: #aaaaaa;

text-align: center;
}
</style>

</head>
<body style='font-family: Microsoft YaHei, Tahoma, Arial, Helvetica, sans-serif; font-size=36px;'>
<div>%text%</div>
</body</html>";
            strHtml = strHtml.Replace("%text%", HttpUtility.HtmlEncode(strText));

            Global.SetHtmlString(this.WebBrowser,
                strHtml,
                this.MainForm.DataDir,
                strTempFileType);
        }

        /// <summary>
        /// 清空 HTML 页
        /// 自动停止先前的异步处理
        /// </summary>
        public void ClearHtmlPage()
        {
            Global.ClearHtmlPage(this.WebBrowser,
                this.MainForm.DataDir,
                this.BackColor);
        }

        Color _backColor = SystemColors.Window;
        public Color BackColor
        {
            get
            {
                return _backColor;
            }
            set
            {
                _backColor = value;
            }
        }

        public void Call(string name)
        {
            if (this.CallFunc != null)
                this.CallFunc(name, new EventArgs());
        }
    }

    class AsyncCall
    {
        public string FuncType = "";    // 功能类型
        // object ContextObject = null;    // 上下文对象

        public object[] InputParameters = null;
    }

    // 
    /// <summary>
    /// 获得资源(下载到本地临时文件)的本地路径事件
    /// </summary>
    /// <param name="sender">发送者</param>
    /// <param name="e">事件参数</param>
    public delegate void GetLocalFilePathEventHandler(object sender,
GetLocalFilePathEventArgs e);

    /// <summary>
    /// 获得资源(下载到本地临时文件)的本地路径事件的参数
    /// </summary>
    public class GetLocalFilePathEventArgs : EventArgs
    {
        /// <summary>
        /// 名称
        /// </summary>
        public string Name = "";
        /// <summary>
        /// 返回本地文件路径
        /// </summary>
        public string LocalFilePath = "";   // 资源本地路径。如果返回null，表示没有这个资源，或者对象已经上载
    }

    /// <summary>
    /// 输出调试信息事件
    /// </summary>
    /// <param name="sender">发送者</param>
    /// <param name="e">事件参数</param>
    public delegate void OutputDebugInfoEventHandler(object sender,
OutputDebugInfoEventArgs e);

    /// <summary>
    /// 输出调试信息事件的参数
    /// </summary>
    public class OutputDebugInfoEventArgs : EventArgs
    {
        /// <summary>
        /// 调试信息文本内容
        /// 建议每次显示为新的一行
        /// </summary>
        public string Text = "";
    }
}
