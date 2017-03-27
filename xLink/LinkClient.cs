﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NewLife;
using NewLife.Net;
using NewLife.Reflection;
using NewLife.Remoting;
using NewLife.Threading;

namespace xLink
{
    /// <summary>物联客户端</summary>
    public class LinkClient : ApiClient
    {
        #region 属性
        /// <summary>远程地址</summary>
        public NetUri Remote { get; set; }

        /// <summary>附加参数</summary>
        public IDictionary<String, Object> Parameters { get; set; } = new Dictionary<String, Object>();

        /// <summary>加密通信指令中负载数据的密匙</summary>
        public Byte[] Key { get; set; }
        #endregion

        #region 构造
        public LinkClient(String uri) : base(uri)
        {
            Remote = uri;

            // 初始数据
            var dic = Parameters;
            dic["OS"] = Runtime.OSName;
            dic["Agent"] = $"{Environment.UserName}_{Environment.MachineName}";

            var asmx = AssemblyX.Create(Assembly.GetCallingAssembly());
            dic["Version"] = asmx.Version;
        }
        #endregion

        #region 打开关闭
        /// <summary>开始处理数据</summary>
        public override Boolean Open()
        {
            if (Active) return true;

            if (Remote == null) throw new ArgumentNullException(nameof(Remote));

            SetRemote(Remote + "");

            var rc4 = new RC4Filter();
            rc4.GetKey = ctx => Key;
            Filters.Add(rc4);

            WriteLog("连接令牌服务器 {0}", Remote);

            if (!base.Open()) return false;

            _timer = new TimerX(TimerCallback, this, 1000, 5000);

            return Active;
        }

        /// <summary>关闭</summary>
        /// <param name="reason"></param>
        public override void Close(String reason)
        {
            _timer.TryDispose();

            base.Close(reason);
        }

        private TimerX _timer;
        private async void TimerCallback(Object state)
        {
            if (Logined) await PingAsync();
        }
        #endregion

        #region 登录
        /// <summary>是否已登录</summary>
        public Boolean Logined { get; protected set; }

        /// <summary>异步登录</summary>
        /// <param name="user"></param>
        /// <param name="pass"></param>
        public async Task<IDictionary<String, Object>> LoginAsync(String user, String pass)
        {
            if (user.IsNullOrEmpty()) throw new ArgumentNullException(nameof(user), "用户名不能为空！");
            //if (pass.IsNullOrEmpty()) throw new ArgumentNullException(nameof(pass), "密码不能为空！");

            // 加密随机数，密码为空表示注册
            var p = "";
            if (!pass.IsNullOrEmpty())
            {
                var salt = ((Int64)(DateTime.Now - new DateTime(1970, 1, 1)).TotalMilliseconds).GetBytes();
                p = salt.RC4(pass.GetBytes()).ToHex();
                p = salt.ToHex() + p;
            }

            // 克隆一份，避免修改原始数据
            var dic = Parameters.ToDictionary(e => e.Key, e => e.Value);
            dic.Merge(new { user, pass = p });

            // 声明响应
            IDictionary<String, Object> rs = null;
            var enc = Encoder;
            var code = 0;
            Object result = null;
            while (true)
            {
                //var rs = await InvokeAsync<IDictionary<String, Object>>("Login", new { user, pass = p });
                rs = await InvokeAsync<IDictionary<String, Object>>("Login", dic);

                enc.TryGet(rs, out code, out result);

                // 检查重定向
                if (code == 302)
                {
                    //OnRedirect(ex.Message);
                    // 解析目标地址，合并参数，断开当前连接，建立新连接
                    if (rs.TryGetValue("Location", out result) && result != null)
                    {
                        dic.Merge(rs);

                        var uri = result + "";
                        Client.TryDispose();
                        SetRemote(uri);

                        // 重新登录
                        continue;
                    }
                }

                break;
            }

            //Token = rs.Token;
            //Key = rs.Key.RC4(pass.GetBytes());

            //LoginTime = DateTime.Now;
            //WriteLog("密匙:{1},令牌到手 0x{0:X8}", Token, Key.ToHex());

            //// 登录完成以后，开始心跳，周期调整
            //SetTimerPeriod(20);

            //// 为自动登录做准备
            //UserName = user;
            //Password = pass;

            Logined = true;

            //// 不管登录成功还是失败，都除法OnLogined事件
            //OnLogined?.Invoke(this, EventArgs.Empty);

            return rs;
        }
        #endregion

        #region 心跳
        /// <summary>时间差</summary>
        public TimeSpan Span { get; private set; }

        /// <summary>发送心跳</summary>
        /// <param name="args"></param>
        /// <returns>是否收到成功响应</returns>
        public virtual async Task<IDictionary<String, Object>> PingAsync(Object args = null)
        {
            var dic = args.ToDictionary();
            if (!dic.ContainsKey("Time")) dic["Time"] = DateTime.Now;

            var rs = await InvokeAsync<IDictionary<String, Object>>("Ping", dic);

            // 获取服务器时间
            Object dt;
            if (rs.TryGetValue("ServerTime", out dt))
            {
                // 保存时间差，这样子以后只需要拿本地当前时间加上时间差，即可得到服务器时间
                Span = dt.ToDateTime() - DateTime.Now;
            }

            return rs;
        }
        #endregion

        #region 业务
        #endregion

        #region 远程调用
        /// <summary>调用</summary>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="action"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public override async Task<TResult> InvokeAsync<TResult>(String action, Object args = null)
        {
            if (!Logined && action != "Login") throw new Exception("未登录，无权调用" + action);

            return await base.InvokeAsync<TResult>(action, args);
        }
        #endregion
    }
}