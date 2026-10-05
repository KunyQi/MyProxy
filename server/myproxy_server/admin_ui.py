"""Static assets for the private MyProxy Server administration UI.

The UI is deliberately dependency-free and served only by the same loopback
listener as the Admin API.  It keeps only the temporary UI session in JavaScript memory and
never persists it in cookies, Web Storage, URLs, or generated markup.
"""

from __future__ import annotations


ADMIN_HTML = """<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="color-scheme" content="light">
  <title>MyProxy Server Management</title>
  <link rel="stylesheet" href="/admin/app.css">
</head>
<body>
  <main class="shell">
    <section id="login-view" class="login" aria-labelledby="login-title">
      <p class="eyebrow">MYPROXY / SERVER</p>
      <h1 id="login-title">Server.</h1>
      <p class="muted">通过私有连接访问。</p>
      <p id="login-help" class="caption">请用 VPS SSH 密钥运行「打开管理后台.cmd」。验证后自动进入，无需输入密码。</p>
      <p id="login-error" class="form-error" role="alert"></p>
    </section>

    <section id="app-view" hidden>
      <header class="app-header">
        <div>
          <p class="eyebrow">MYPROXY / SERVER</p>
          <h1 id="workspace-title">状态</h1>
        </div>
        <div class="header-actions">
          <span id="service-state" class="status">正在检查</span>
          <button id="refresh-button" class="secondary" type="button">刷新</button>
          <button id="logout-button" class="secondary" type="button">退出</button>
        </div>
      </header>

      <nav class="section-nav" aria-label="管理栏目">
        <button type="button" class="nav-button is-active" data-view="overview" aria-pressed="true">状态</button>
        <button type="button" class="nav-button" data-view="users" aria-pressed="false">用户</button>
        <button type="button" class="nav-button" data-view="bindings" aria-pressed="false">配对码</button>
        <button type="button" class="nav-button" data-view="devices" aria-pressed="false">设备</button>
        <button type="button" class="nav-button" data-view="operations" aria-pressed="false">版本与发布</button>
        <button type="button" class="nav-button" data-view="releases" aria-pressed="false">发布指派</button>
        <button type="button" class="nav-button" data-view="usage" aria-pressed="false">用量与活动</button>
      </nav>

      <p id="page-message" class="page-message" role="status" aria-live="polite"></p>

      <section id="overview-section" class="view-section" aria-labelledby="overview-title">
        <div class="section-heading">
          <div>
            <h2 id="overview-title">状态</h2>
            <p class="muted">服务状态与当前公开配置摘要。</p>
          </div>
        </div>
        <dl id="overview-list" class="summary-list"></dl>
      </section>

      <section id="users-section" class="view-section" aria-labelledby="users-title" hidden>
        <div class="section-heading">
          <div>
            <h2 id="users-title">用户</h2>
            <p class="muted">创建用户并管理其访问资格。</p>
          </div>
        </div>
        <form id="user-form" class="tool-form">
          <div class="field">
            <label for="username-input">用户名</label>
            <input id="username-input" maxlength="64" required autocomplete="off">
          </div>
          <div class="field">
            <label for="display-name-input">显示名称</label>
            <input id="display-name-input" maxlength="64" autocomplete="off">
          </div>
          <button class="primary form-action" type="submit">创建用户</button>
        </form>
        <div class="table-wrap">
          <table>
            <thead><tr><th>用户</th><th>状态</th><th>创建时间</th><th class="actions-column">操作</th></tr></thead>
            <tbody id="users-body"></tbody>
          </table>
        </div>
        <p id="users-empty" class="empty" hidden>暂无用户。</p>
      </section>

      <section id="bindings-section" class="view-section" aria-labelledby="bindings-title" hidden>
        <div class="section-heading">
          <div>
            <h2 id="bindings-title">配对码</h2>
            <p class="muted">签发、复制、延期或撤销一次性配对码。</p>
          </div>
          <label class="compact-filter">筛选用户
            <select id="binding-filter"><option value="">全部用户</option></select>
          </label>
        </div>
        <form id="binding-form" class="tool-form four-columns">
          <div class="field">
            <label for="binding-user">用户</label>
            <select id="binding-user" required><option value="">请选择</option></select>
          </div>
          <div class="field">
            <label for="device-template">设备类型</label>
            <select id="device-template"><option value="windows">Windows</option><option value="android">Android</option><option value="linux">Linux</option></select>
          </div>
          <div class="field">
            <label for="binding-ttl">有效期</label>
            <select id="binding-ttl"><option value="3600">1 小时</option><option value="86400">1 天</option><option value="604800">7 天</option></select>
          </div>
          <button class="primary form-action" type="submit">生成配对码</button>
        </form>
        <div id="new-binding-result" class="one-time-result" role="status" hidden>
          <div>
            <p class="primary-line">新配对码（仅本次显示）</p>
            <p class="caption">复制并安全发送给对应用户；关闭后不能从列表恢复明文。</p>
          </div>
          <code id="new-binding-code" class="pairing-code"></code>
          <div class="row-actions">
            <button id="copy-new-binding" class="primary small" type="button">复制配对码</button>
            <button id="dismiss-new-binding" class="secondary small" type="button">关闭</button>
          </div>
        </div>
        <div class="table-wrap">
          <table>
            <thead><tr><th>记录 ID</th><th>用户</th><th>设备</th><th>状态</th><th>到期时间</th><th class="actions-column">操作</th></tr></thead>
            <tbody id="bindings-body"></tbody>
          </table>
        </div>
        <p id="bindings-empty" class="empty" hidden>暂无配对码。</p>
      </section>

      <section id="devices-section" class="view-section" aria-labelledby="devices-title" hidden>
        <div class="section-heading">
          <div>
            <h2 id="devices-title">设备</h2>
            <p class="muted">查看在线活动并禁用失效设备。</p>
          </div>
          <label class="compact-filter">筛选用户
            <select id="device-filter"><option value="">全部用户</option></select>
          </label>
        </div>
        <div class="table-wrap">
          <table>
            <thead><tr><th>设备</th><th>用户</th><th>平台</th><th>状态</th><th>最后活动</th><th class="actions-column">操作</th></tr></thead>
            <tbody id="devices-body"></tbody>
          </table>
        </div>
        <p id="devices-empty" class="empty" hidden>暂无设备。</p>
      </section>

      <section id="operations-section" class="view-section" aria-labelledby="operations-title" hidden>
        <div class="section-heading">
          <div>
            <h2 id="operations-title">版本与发布</h2>
            <p class="muted">提升配置版本，维护 Windows、Android 与 Linux 更新元数据。</p>
          </div>
        </div>
        <form id="config-form" class="inline-operation">
          <div class="field grow">
            <label for="config-note">配置版本备注</label>
            <input id="config-note" maxlength="256" placeholder="例如：同步 3x-ui 配置" autocomplete="off">
          </div>
          <button class="secondary form-action" type="submit">提升配置版本</button>
        </form>
        <div class="release-block">
          <h3>Windows 更新</h3>
          <form id="windows-release-form" class="release-form" data-platform="windows">
            <div class="field"><label for="windows-version">版本</label><input id="windows-version" required pattern="[0-9]+\\.[0-9]+\\.[0-9]+" placeholder="0.2.0"></div>
            <div class="field wide"><label for="windows-url">下载地址</label><input id="windows-url" type="url" autocomplete="off"></div>
            <div class="field wide"><label for="windows-sha">SHA-256</label><input id="windows-sha" pattern="([0-9a-f]{64})?" maxlength="64" class="mono" autocomplete="off"></div>
            <label class="checkbox"><input id="windows-mandatory" type="checkbox">强制更新</label>
            <button class="primary" type="submit">保存 Windows 发布信息</button>
          </form>
        </div>
        <div class="release-block">
          <h3>Android 更新</h3>
          <form id="android-release-form" class="release-form" data-platform="android">
            <div class="field"><label for="android-version">版本</label><input id="android-version" required pattern="[0-9]+\\.[0-9]+\\.[0-9]+" placeholder="0.2.0"></div>
            <div class="field wide"><label for="android-url">下载地址</label><input id="android-url" type="url" autocomplete="off"></div>
            <div class="field wide"><label for="android-sha">SHA-256</label><input id="android-sha" pattern="([0-9a-f]{64})?" maxlength="64" class="mono" autocomplete="off"></div>
            <label class="checkbox"><input id="android-mandatory" type="checkbox">强制更新</label>
            <button class="primary" type="submit">保存 Android 发布信息</button>
          </form>
        </div>
        <div class="release-block">
          <h3>Linux 更新</h3>
          <form id="linux-release-form" class="release-form" data-platform="linux">
            <div class="field"><label for="linux-version">版本</label><input id="linux-version" required pattern="[0-9]+\\.[0-9]+\\.[0-9]+" placeholder="0.2.0"></div>
            <div class="field wide"><label for="linux-url">下载地址（tarball）</label><input id="linux-url" type="url" autocomplete="off"></div>
            <div class="field wide"><label for="linux-sha">SHA-256</label><input id="linux-sha" pattern="([0-9a-f]{64})?" maxlength="64" class="mono" autocomplete="off"></div>
            <label class="checkbox"><input id="linux-mandatory" type="checkbox">强制更新</label>
            <button class="primary" type="submit">保存 Linux 发布信息</button>
          </form>
        </div>
      </section>

      <section id="releases-section" class="view-section" aria-labelledby="releases-title" hidden>
        <div class="section-heading">
          <div>
            <h2 id="releases-title">发布指派</h2>
            <p class="muted">选择本地安装包，在浏览器签署发布清单，再上传和指派。</p>
          </div>
        </div>

        <div class="release-wizard">
          <h3>发布新版本</h3>
          <p class="muted">Windows 请选择包含已签名 MyProxy.exe 的 ZIP，Android 请选择已签名 APK，Linux 请选择 tar.gz 包。</p>
          <p id="wizard-environment" class="callout" role="status"></p>
          <form id="release-wizard-form" class="wizard-fields">
            <div class="field wizard-wide"><label for="wizard-file">本地安装包</label><input id="wizard-file" type="file" accept=".zip,.apk,.tar.gz,.tgz" required></div>
            <div class="field"><label for="wizard-platform">平台</label><select id="wizard-platform"><option value="windows">Windows</option><option value="android">Android</option><option value="linux">Linux</option></select></div>
            <div class="field"><label for="wizard-version">版本</label><input id="wizard-version" required pattern="[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.\\-]+)?(?:\\+[0-9A-Za-z.\\-]+)?" placeholder="0.2.0 或 0.2.0-beta.1" autocomplete="off"></div>
            <div class="field"><label for="wizard-channel">通道</label><select id="wizard-channel"><option value="stable">稳定版</option><option value="beta">测试版</option></select></div>
            <div class="field"><label for="wizard-minimum">最低可升级版本（可选）</label><input id="wizard-minimum" pattern="[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.\\-]+)?(?:\\+[0-9A-Za-z.\\-]+)?" placeholder="例如 0.1.0" autocomplete="off"></div>
            <div id="wizard-signer-field" class="field wizard-wide"><label for="wizard-subject">安装包签名证书 SHA-256</label><input id="wizard-subject" class="mono" maxlength="64" pattern="[0-9a-fA-F]{64}" autocomplete="off"><p id="wizard-signer-help" class="caption"></p></div>
            <label class="checkbox wizard-wide"><input id="wizard-mandatory" type="checkbox">标记为必须更新</label>
            <div class="field"><label for="wizard-key-id">清单签名 keyId</label><input id="wizard-key-id" maxlength="64" required placeholder="rel-2026a" autocomplete="off"></div>
            <div class="field"><label for="wizard-key-file">发布私钥（PKCS8 PEM / DER）</label><input id="wizard-key-file" type="file" accept=".pem,.der,.key"></div>
            <div class="wizard-wide wizard-actions"><button id="wizard-generate-key" class="secondary" type="button">生成本地 Ed25519 密钥</button><button id="wizard-backup-key" class="secondary" type="button" disabled>下载私钥备份</button></div>
            <div class="field wizard-wide"><label for="wizard-public-key">发布公钥（配置到服务端与客户端）</label><input id="wizard-public-key" class="mono" readonly placeholder="选择私钥或生成后显示"><p id="wizard-key-status" class="caption" role="status">私钥只留在当前浏览器内存；不会上传。刷新或退出会清除它。</p></div>
            <details class="wizard-wide"><summary>公钥配置说明</summary><p class="caption">在服务的 systemd 覆盖配置（drop-in）中设置 MYPROXY_RELEASE_SIGNING_KEYS，使用下面的 keyId:公钥；各客户端也需嵌入同一发布公钥。保存配置并重启服务后，点击「刷新」。服务端只能验签；私钥由发布者保存。</p><input id="wizard-key-config" class="mono" readonly aria-label="发布公钥配置"><div class="wizard-actions"><button id="wizard-copy-key" class="secondary" type="button">复制发布公钥配置</button></div><label for="wizard-linux-key-config">Linux 产物信任配置（原始公钥 SHA-256:公钥）</label><input id="wizard-linux-key-config" class="mono" readonly><div class="wizard-actions"><button id="wizard-copy-linux-key" class="secondary" type="button">复制 Linux 产物公钥配置</button></div><p class="caption">Linux 客户端还需信任此产物公钥。登记、发布和指派只管理服务端发布状态；客户端收到通知与安装取决于对应版本是否接入更新流程。</p></details>
            <div class="field wizard-wide"><label for="wizard-external-url">外部下载地址（仅在不上传到此服务器时使用）</label><input id="wizard-external-url" type="url" placeholder="https://releases.example/MyProxy.zip" autocomplete="off"></div>
            <label class="checkbox wizard-wide"><input id="wizard-managed" type="checkbox" checked>上传到此服务器的发布存储</label>
            <div class="field"><label for="wizard-scope">发布后的指派范围</label><select id="wizard-scope"><option value="none">仅发布，暂不指派</option><option value="device">指定设备</option><option value="user">指定用户</option><option value="platform">此平台全部设备</option></select></div>
            <div class="field"><label for="wizard-target">指派目标</label><select id="wizard-target"><option value="">无需目标</option></select></div>
            <div class="field wizard-wide"><label for="wizard-note">备注（可选）</label><input id="wizard-note" maxlength="256" autocomplete="off"></div>
            <div class="wizard-wide wizard-actions"><button id="wizard-prepare" class="secondary" type="submit">计算并签署，查看发布摘要</button><button id="wizard-reset" class="secondary" type="button">开始另一个发布</button></div>
          </form>
          <div id="wizard-review" hidden>
            <h3>发布摘要</h3>
            <dl id="wizard-summary" class="summary-list"></dl>
            <details><summary>查看已签名清单</summary><pre id="wizard-manifest" class="manifest-preview mono"></pre></details>
            <p id="wizard-progress" class="callout" role="status" aria-live="polite"></p>
            <div class="wizard-actions"><button id="wizard-export" class="secondary" type="button" disabled>导出签名清单</button><button id="wizard-publish" class="primary" type="button" disabled>上传并发布</button></div>
          </div>
        </div>

        <details class="advanced-release"><summary>高级：手工登记已签名清单</summary>
        <p class="caption">原样粘贴离线签名产出的 base64 清单。服务端未配置对应公钥时拒绝登记。</p>
        <form id="release-form" class="tool-form">
          <div class="field wide">
            <label for="release-manifest">manifest（base64）</label>
            <input id="release-manifest" class="mono" required autocomplete="off"
                   placeholder="由发布流程产出，原样粘贴，不要重新格式化">
          </div>
          <div class="field wide">
            <label for="release-signature">签名（base64）</label>
            <input id="release-signature" class="mono" required autocomplete="off">
          </div>
          <div class="field">
            <label for="release-key-id">签名 keyId</label>
            <input id="release-key-id" required autocomplete="off" placeholder="rel-2026a">
          </div>
          <div class="field">
            <label for="release-note">备注</label>
            <input id="release-note" maxlength="256" autocomplete="off">
          </div>
          <button class="primary form-action" type="submit">登记 release</button>
        </form>
        </details>

        <div class="table-wrap">
          <table>
            <thead><tr><th>版本</th><th>平台</th><th>通道</th><th>状态</th><th>SHA-256</th><th class="actions-column">操作</th></tr></thead>
            <tbody id="releases-body"></tbody>
          </table>
        </div>
        <p id="releases-empty" class="empty" hidden>暂无已登记的 release。</p>

        <div class="release-block">
          <h3>指派</h3>
          <p class="muted">
            指派只表达「谁拿哪个 release」加一张扁平开关表，<strong>不能携带 URL、脚本或命令</strong>。
            解析顺序 device → user → platform，先命中者生效；撤销一个 release 会让指向它的指派
            向下一层回落，并停止继续下发已撤销版本。已安装的客户端不会因此自动降级。
          </p>
          <form id="assignment-form" class="tool-form">
            <div class="field">
              <label for="assignment-scope">范围</label>
              <select id="assignment-scope">
                <option value="platform">平台默认</option>
                <option value="user">按用户</option>
                <option value="device">按设备</option>
              </select>
            </div>
            <div class="field wide">
              <label for="assignment-target">目标</label>
              <select id="assignment-target"><option value="">（平台默认无需目标）</option></select>
            </div>
            <div class="field">
              <label for="assignment-platform">平台</label>
              <select id="assignment-platform">
                <option value="windows">windows</option>
                <option value="android">android</option>
                <option value="linux">linux</option>
              </select>
            </div>
            <div class="field wide">
              <label for="assignment-release">release</label>
              <select id="assignment-release"><option value="">（只设开关，不锁版本）</option></select>
            </div>
            <div class="field wide">
              <label for="assignment-flags">feature flags（JSON 对象）</label>
              <input id="assignment-flags" class="mono" autocomplete="off"
                     placeholder='{"usageCategories": true}'>
            </div>
            <button class="primary form-action" type="submit">保存指派</button>
          </form>

          <div class="table-wrap">
            <table>
              <thead><tr><th>范围</th><th>目标</th><th>平台</th><th>release</th><th>flags</th><th class="actions-column">操作</th></tr></thead>
              <tbody id="assignments-body"></tbody>
            </table>
          </div>
          <p id="assignments-empty" class="empty" hidden>暂无指派。</p>
        </div>

        <div class="release-block">
          <h3>审计流水</h3>
          <div class="table-wrap">
            <table>
              <thead><tr><th>时间</th><th>动作</th><th>范围</th><th>目标</th><th>release</th><th>详情</th></tr></thead>
              <tbody id="audit-body"></tbody>
            </table>
          </div>
          <p id="audit-empty" class="empty" hidden>暂无审计记录。</p>
        </div>
      </section>

      <section id="usage-section" class="view-section" aria-labelledby="usage-title" hidden>
        <div class="section-heading">
          <div>
            <h2 id="usage-title">用量与活动</h2>
            <p class="muted">聚合流量、时间趋势、服务类别与粗略位置。</p>
          </div>
          <label class="compact-filter">筛选设备
            <select id="usage-device"><option value="">全部设备</option></select>
          </label>
        </div>

        <p class="callout">
          用量部分只有<strong>聚合数据</strong>：没有主机名、没有 URL、没有单条连接记录。
          但<strong>出口 IP 是完整记录的</strong>——当前地址，外加每台设备最近若干个
          不同地址。这是一份可归属到个人的连接记录，数据库备份与导出请按敏感数据对待。
          country / city / ISP 由管理员带外填写——服务端没有 GeoIP 数据库，
          也不会把用户地址送去第三方定位服务换一个回来。
        </p>

        <div class="release-block">
          <h3>活动状态</h3>
          <div class="table-wrap">
            <table>
              <thead><tr><th>设备</th><th>用户</th><th>状态</th><th>最后活动</th><th>出口 IP</th><th>位置</th><th class="actions-column">操作</th></tr></thead>
              <tbody id="activity-body"></tbody>
            </table>
          </div>
          <p id="activity-empty" class="empty" hidden>暂无设备。</p>
        </div>

        <div class="release-block">
          <h3>用量趋势</h3>
          <form id="usage-form" class="tool-form">
            <div class="field">
              <label for="usage-granularity">粒度</label>
              <select id="usage-granularity">
                <option value="hour">按小时（最近 24 小时）</option>
                <option value="day">按天（最近 30 天）</option>
              </select>
            </div>
            <button class="secondary form-action" type="submit">查询</button>
          </form>
          <dl id="usage-summary" class="summary-list"></dl>
          <div class="table-wrap">
            <table>
              <thead><tr><th>时间桶</th><th>上行</th><th>下行</th><th>合计</th></tr></thead>
              <tbody id="usage-body"></tbody>
            </table>
          </div>
          <p id="usage-empty" class="empty" hidden>该时间窗内没有数据。</p>
        </div>

        <div class="release-block">
          <h3>服务类别</h3>
          <div class="table-wrap">
            <table>
              <thead><tr><th>类别</th><th>字节</th></tr></thead>
              <tbody id="categories-body"></tbody>
            </table>
          </div>
          <p id="categories-empty" class="empty" hidden>
            暂无归因数据。类别归因需要为设备打开 <code>usageCategories</code> 开关。
          </p>
        </div>
      </section>
    </section>
  </main>
  <div id="toast" class="toast" role="status" aria-live="polite" hidden></div>
  <script src="/admin/release-signing.js" defer></script>
  <script src="/admin/app.js" defer></script>
</body>
</html>
""".encode("utf-8")


ADMIN_CSS = """
:root {
  color-scheme:light;
  font-family:"Segoe UI","Microsoft YaHei UI",system-ui,sans-serif;
  --background:#f5f4f0; --surface:#faf9f6; --text:#30332f; --muted:#6f746c;
  --border:#d9dcd3; --accent:#424e3c; --danger:#97483e;
}
* { box-sizing:border-box; }
[hidden] { display:none !important; }
html { min-width:320px; background:var(--background); }
body { margin:0; color:var(--text); font-size:14px; line-height:1.65; }
button,input,select { font:inherit; color:inherit; border-radius:2px; min-height:40px; }
button { cursor:pointer; border:1px solid var(--border); padding:8px 16px; background:transparent; font-weight:500; }
button:hover { background:#eaece4; border-color:#a8aea0; }
button:disabled { opacity:.45; cursor:wait; }
button:focus-visible,input:focus-visible,select:focus-visible { outline:2px solid #596b4a; outline-offset:4px; }
input,select { width:100%; padding:9px 11px; border:1px solid #c6cbbf; background:var(--surface); }
input:hover,select:hover { border-color:#929b88; }
label { font-size:12px; color:#575e51; }
h1,h2,h3,p { margin-top:0; }
h1 { font-size:36px; font-weight:450; letter-spacing:-1.5px; line-height:1.2; margin-bottom:0; }
h2 { font-size:19px; font-weight:500; margin-bottom:8px; }
h3 { font-size:14px; font-weight:600; margin-bottom:18px; }
.shell { width:min(1120px,calc(100% - 96px)); margin:0 auto; padding:64px 0 100px; }
.eyebrow { color:var(--muted); font:11px ui-monospace,Consolas,monospace; letter-spacing:2px; margin-bottom:22px; }
.muted,.caption,.secondary-line { color:var(--muted); }
.muted { margin-bottom:0; font-size:13px; }
.caption,.secondary-line { font-size:12px; }
.caption { margin:0; }
.app-header { display:flex; align-items:flex-end; justify-content:space-between; gap:24px; padding-bottom:40px; }
.header-actions { display:flex; align-items:center; flex-wrap:wrap; gap:10px; }
.header-actions button { border-color:transparent; padding:6px 10px; font-size:12px; }
.status { display:inline-flex; align-items:center; gap:8px; font-size:12px; color:var(--muted); margin-right:20px; }
.status::before { content:""; width:5px; height:5px; background:currentColor; border-radius:50%; }
.status.is-online { color:#526245; }
.status.is-error,.form-error,.page-message.is-error { color:var(--danger); }
.section-nav { display:flex; gap:26px; border-top:1px solid var(--border); border-bottom:1px solid var(--border); overflow-x:auto; margin-bottom:42px; }
.nav-button { flex:0 0 auto; border:0; border-radius:0; padding:16px 0; color:var(--muted); position:relative; }
.nav-button:hover { background:none; color:var(--text); }
.nav-button.is-active { color:var(--text); box-shadow:inset 0 -2px var(--accent); }
.page-message { color:var(--muted); font-size:12px; margin:0 0 24px; }
.page-message:empty { display:none; }
.view-section { min-width:0; }
.section-heading { display:flex; align-items:flex-end; justify-content:space-between; gap:20px; margin-bottom:30px; }
#overview-section .section-heading h2 { display:none; }
.summary-list { margin:0; }
.summary-row { display:grid; grid-template-columns:220px minmax(0,1fr); gap:24px; border-bottom:1px solid var(--border); padding:21px 0; }
.summary-row:first-child { border-top:1px solid var(--border); }
.summary-row dt { color:var(--muted); font-size:12px; }
.summary-row dd { margin:0; overflow-wrap:anywhere; font-family:ui-monospace,"Microsoft YaHei UI",monospace; font-size:13px; }
.primary { background:var(--accent); border-color:var(--accent); color:#f9faf6; }
.primary:hover { background:#323f2c; color:white; }
.secondary { background:transparent; }
.danger { color:var(--danger); border-color:#d2bcb5; }
.danger:hover { background:#f0e3df; border-color:#ad7869; }
.small { font-size:12px; min-height:32px; padding:5px 10px; }
.tool-form { display:grid; grid-template-columns:minmax(150px,1fr) minmax(150px,1fr) auto; gap:20px; align-items:end; padding:24px 0; border-top:1px solid var(--border); border-bottom:1px solid var(--border); margin-bottom:32px; }
.four-columns { grid-template-columns:minmax(160px,1.2fr) minmax(120px,1fr) minmax(100px,.8fr) auto; }
.field { display:grid; gap:9px; min-width:0; }
.grow { flex:1; }
.form-action { white-space:nowrap; }
.compact-filter { display:grid; grid-template-columns:auto 180px; align-items:center; gap:12px; }
.inline-operation { display:flex; gap:20px; align-items:end; padding-bottom:30px; border-bottom:1px solid var(--border); }
.table-wrap { width:100%; overflow-x:auto; }
table { width:100%; border-collapse:collapse; min-width:720px; }
th,td { text-align:left; padding:17px 14px; border-bottom:1px solid var(--border); vertical-align:top; }
th { color:var(--muted); font-size:11px; font-weight:500; white-space:nowrap; }
td { font-size:13px; }
th:first-child,td:first-child { padding-left:0; }
th:last-child,td:last-child { padding-right:0; }
tbody tr:hover { background:#eeefe8; }
.actions-column { text-align:right; }
.row-actions { display:flex; flex-wrap:wrap; justify-content:flex-end; gap:7px; min-width:142px; }
.primary-line { font-weight:500; }
.secondary-line { margin-top:4px; }
.mono,code { font-family:ui-monospace,Consolas,monospace; }
.badge { display:inline-block; color:var(--muted); font-size:12px; }
.badge::before { content:"·"; margin-right:6px; }
.badge.active { color:#536b41; }
.badge.error { color:var(--danger); }
.empty { padding:40px 0; margin:0; color:var(--muted); font-size:13px; text-align:left; border-bottom:1px solid var(--border); }
.one-time-result { display:flex; align-items:center; justify-content:space-between; gap:22px; padding:22px 0 22px 18px; border-left:2px solid #889877; margin-bottom:30px; }
.one-time-result p { margin-bottom:3px; }
.pairing-code { font-size:24px; letter-spacing:2px; white-space:nowrap; }
.callout { color:var(--muted); font-size:12px; line-height:1.85; padding:0 0 0 16px; border-left:2px solid var(--border); margin-bottom:26px; max-width:850px; }
.release-block { padding-top:36px; }
.release-block+.release-block { margin-top:30px; border-top:1px solid var(--border); }
.release-form { display:grid; grid-template-columns:minmax(150px,.6fr) minmax(220px,1.4fr); gap:20px; align-items:end; }
.release-form button { justify-self:start; }
.release-wizard { border-top:1px solid var(--border); padding-top:24px; margin-bottom:40px; }
.wizard-fields { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:20px; margin:24px 0; }
.wizard-wide { grid-column:1/-1; }
.wizard-actions { display:flex; flex-wrap:wrap; gap:10px; margin:18px 0; }
.wizard-fields input[type=file] { font-size:12px; }
.wizard-fields input[type=file]::file-selector-button { font:inherit; border:0; border-right:1px solid var(--border); padding:5px 10px 5px 0; margin-right:12px; color:var(--text); background:transparent; cursor:pointer; }
.manifest-preview { white-space:pre-wrap; overflow-wrap:anywhere; padding:16px; font-size:12px; border:1px solid var(--border); }
.advanced-release { margin:32px 0; }
summary { cursor:pointer; font-size:13px; margin-bottom:12px; }
.checkbox { display:inline-flex; align-items:center; gap:10px; min-height:40px; }
.checkbox input { width:16px; min-height:16px; margin:0; }
.toast { position:fixed; right:24px; bottom:24px; padding:14px 20px; max-width:calc(100% - 48px); background:#30392a; color:#fff; font-size:13px; z-index:10; }
.login { width:min(380px,100%); margin:12vh auto 0; }
.login h1 { font-size:64px; font-weight:400; letter-spacing:-3px; }
.login>.muted { margin-top:24px; }
.login-form { display:grid; gap:12px; border-top:1px solid var(--border); margin-top:42px; padding-top:28px; }
.login-form .primary { margin-top:18px; justify-self:start; min-width:100px; }
.form-error { min-height:20px; margin:0; font-size:12px; }
/* Motion stays brief and never delays interaction or announces false activity. */
button { transition:background-color 140ms ease,border-color 140ms ease,color 140ms ease,transform 100ms ease; }
button:not(:disabled):active { transform:translateY(1px); }
input,select { transition:border-color 160ms ease,background-color 160ms ease; }
tbody tr { transition:background-color 140ms ease; }
.nav-button.is-active { box-shadow:none; }
.nav-button::after { content:""; position:absolute; left:0; right:0; bottom:0; height:2px; background:var(--accent); transform:scaleX(0); transform-origin:left; transition:transform 180ms ease; }
.nav-button.is-active::after { transform:scaleX(1); }
.login:not([hidden]) { animation:quiet-enter 300ms ease-out both; }
#app-view:not([hidden]) .app-header { animation:quiet-enter 240ms ease-out both; }
.view-section:not([hidden]) { animation:quiet-enter 200ms ease-out both; }
.view-section:not([hidden]) .summary-row { animation:quiet-enter 220ms ease-out both; }
.summary-row:nth-child(2) { animation-delay:20ms !important; }
.summary-row:nth-child(3) { animation-delay:40ms !important; }
.summary-row:nth-child(4) { animation-delay:60ms !important; }
.summary-row:nth-child(n+5) { animation-delay:80ms !important; }
.toast:not([hidden]),.one-time-result:not([hidden]) { animation:quiet-enter 180ms ease-out both; }
.page-message:not(:empty),.form-error:not(:empty) { animation:quiet-fade 160ms ease-out both; }
@keyframes quiet-enter { from { opacity:0; transform:translateY(4px); } to { opacity:1; transform:translateY(0); } }
@keyframes quiet-fade { from { opacity:0; } to { opacity:1; } }
@media(prefers-reduced-motion:reduce) {
  *,*::before,*::after { animation:none !important; transition:none !important; }
  button:not(:disabled):active { transform:none; }
}
@media(max-width:960px) { .shell { width:calc(100% - 48px); } .tool-form,.four-columns { grid-template-columns:repeat(2,minmax(0,1fr)); } .section-nav { gap:22px; } }
@media(max-width:600px) {
  .shell { width:calc(100% - 40px); padding:32px 0 60px; }
  h1 { font-size:30px; }
  .app-header { align-items:flex-start; flex-direction:column; padding-bottom:24px; gap:22px; }
  .header-actions { width:100%; }
  .status { margin-right:auto; }
  .section-nav { margin-bottom:30px; gap:24px; }
  .nav-button { font-size:13px; }
  .section-heading { align-items:stretch; flex-direction:column; gap:18px; }
  .summary-row { grid-template-columns:1fr; gap:7px; padding:18px 0; }
  .tool-form,.four-columns,.release-form { grid-template-columns:1fr; }
  .wizard-fields { grid-template-columns:1fr; }
  .wizard-actions button { flex:1 1 100%; }
  .compact-filter { grid-template-columns:auto 1fr; }
  .inline-operation,.one-time-result { flex-direction:column; align-items:stretch; }
  .form-action { width:100%; }
  .login { margin-top:9vh; }
}
""".encode("utf-8")


ADMIN_JS = r"""(() => {
  "use strict";

  const byId = (id) => document.getElementById(id);
  const state = {
    token: "",
    health: null,
    users: [],
    bindings: [],
    devices: [],
    config: null,
    latest: { windows: null, android: null, linux: null },
    releases: [],
    releaseSettings: null,
    assignments: [],
    audit: [],
    activity: [],
    usage: null,
    newBindingCode: "",
    activeView: "overview",
  };

  const views = ["overview", "users", "bindings", "devices", "operations", "releases", "usage"];
  const loginView = byId("login-view");
  const appView = byId("app-view");
  const loginError = byId("login-error");
  const pageMessage = byId("page-message");
  const serviceState = byId("service-state");
  const toast = byId("toast");
  let toastTimer = 0;
  const wizard = {
    key: null, prepared: null, snapshot: null, release: null,
    uploaded: false, signatureUploaded: false, published: false, assigned: false,
    busy: false, revision: 0, appliedFlags: null, flagsChanged: false,
  };

  function text(element, value) {
    element.textContent = value == null ? "" : String(value);
  }

  function element(tag, className, value) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (value != null) text(node, value);
    return node;
  }

  function showToast(message) {
    window.clearTimeout(toastTimer);
    text(toast, message);
    toast.hidden = false;
    toastTimer = window.setTimeout(() => { toast.hidden = true; }, 2800);
  }

  function showPageMessage(message, isError = false) {
    text(pageMessage, message);
    pageMessage.classList.toggle("is-error", isError);
  }

  function setBusy(button, busy, busyLabel) {
    if (!button.dataset.defaultLabel) button.dataset.defaultLabel = button.textContent;
    button.disabled = busy;
    text(button, busy ? busyLabel : button.dataset.defaultLabel);
  }

  function errorMessage(error) {
    return error instanceof Error && error.message ? error.message : "操作失败，请稍后重试";
  }

  function lock(message) {
    state.token = "";
    state.newBindingCode = "";
    byId("new-binding-result").hidden = true;
    text(byId("new-binding-code"), "");
    appView.hidden = true;
    loginView.hidden = false;
    text(loginError, message || "");
    clearWizard(true);
  }

  async function api(path, options = {}) {
    const headers = { Accept: "application/json" };
    if (state.token) headers.Authorization = `Bearer ${state.token}`;
    const hasRawBody = Object.prototype.hasOwnProperty.call(options, "rawBody");
    if (hasRawBody) {
      headers["Content-Type"] = "application/octet-stream";
    } else if (Object.prototype.hasOwnProperty.call(options, "body")) {
      headers["Content-Type"] = "application/json";
    }
    const response = await fetch(path, {
      method: options.method || "GET",
      headers,
      body: hasRawBody ? options.rawBody : Object.prototype.hasOwnProperty.call(options, "body")
        ? JSON.stringify(options.body)
        : undefined,
      cache: "no-store",
      credentials: "omit",
      redirect: "error",
      referrerPolicy: "no-referrer",
    });
    let payload = {};
    const raw = await response.text();
    if (raw) {
      try { payload = JSON.parse(raw); }
      catch (_) { throw new Error("服务器响应格式无效"); }
    }
    if (response.status === 401) {
      lock("会话已失效，请重新运行「打开管理后台.cmd」");
      throw new Error("管理员校验失败");
    }
    if (!response.ok) {
      const message = payload && payload.error && payload.error.message;
      const error = new Error(message || `请求失败（HTTP ${response.status}）`);
      error.status = response.status;
      error.code = payload && payload.error && payload.error.code;
      throw error;
    }
    return payload;
  }

  function userName(userId) {
    const user = state.users.find((item) => item.id === userId);
    return user ? (user.displayName || user.username) : userId;
  }

  function formatTime(value) {
    if (!value) return "—";
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return String(value);
    return new Intl.DateTimeFormat("zh-CN", {
      year: "numeric", month: "2-digit", day: "2-digit",
      hour: "2-digit", minute: "2-digit", hour12: false,
    }).format(date);
  }

  function badge(value) {
    const labels = {
      active: "有效", claimed: "已使用", expired: "已过期",
      revoked: "已撤销", disabled: "已禁用", deleted: "已删除",
    };
    const node = element("span", "badge", labels[value] || value || "未知");
    if (value === "active") node.classList.add("active");
    if (value === "disabled" || value === "revoked") node.classList.add("error");
    return node;
  }

  function detailCell(primary, secondary, mono = false) {
    const cell = document.createElement("td");
    const first = element("div", `primary-line${mono ? " mono" : ""}`, primary || "—");
    cell.append(first);
    if (secondary) cell.append(element("div", "secondary-line", secondary));
    return cell;
  }

  function actionButton(label, action, id, dangerous = false) {
    const button = element("button", `${dangerous ? "danger" : "secondary"} small`, label);
    button.type = "button";
    button.dataset.action = action;
    button.dataset.id = id;
    return button;
  }

  function actionsCell(buttons) {
    const cell = document.createElement("td");
    cell.className = "actions-column";
    const wrap = element("div", "row-actions");
    wrap.append(...buttons);
    cell.append(wrap);
    return cell;
  }

  function populateUserSelect(select, leadingLabel) {
    const previous = select.value;
    const options = [new Option(leadingLabel, "")];
    for (const user of state.users) {
      options.push(new Option(user.displayName || user.username, user.id));
    }
    select.replaceChildren(...options);
    if ([...select.options].some((option) => option.value === previous)) {
      select.value = previous;
    }
  }

  function renderOverview() {
    const list = byId("overview-list");
    const config = state.config || {};
    const profile = config.config || {};
    const rows = [
      ["服务", state.health && state.health.ok ? `正常 · ${state.health.version}` : "不可用"],
      ["控制面入口", "HTTPS 127.0.0.1:1820（仅 SSH 隧道）"],
      ["用户 / 配对码 / 设备", `${state.users.length} / ${state.bindings.length} / ${state.devices.length}`],
      ["配置版本", config.configVersion ? `v${config.configVersion} · ${formatTime(config.createdAt)}` : "—"],
      ["数据节点", profile.server ? `${profile.server}:${profile.port}` : "—"],
      ["协议", profile.security ? `${profile.security} · ${profile.flow || "—"}` : "—"],
      ["SNI", profile.sni || "—"],
    ];
    list.replaceChildren(...rows.map(([name, value]) => {
      const row = element("div", "summary-row");
      row.append(element("dt", "", name), element("dd", "", value));
      return row;
    }));
  }

  function renderUsers() {
    const body = byId("users-body");
    const rows = state.users.map((user) => {
      const row = document.createElement("tr");
      row.append(
        detailCell(user.displayName || user.username, user.username),
        (() => { const cell = document.createElement("td"); cell.append(badge(user.status)); return cell; })(),
        detailCell(formatTime(user.createdAt), ""),
        actionsCell([
          actionButton("签发配对码", "issue-binding", user.id),
          actionButton("删除", "delete-user", user.id, true),
        ]),
      );
      return row;
    });
    body.replaceChildren(...rows);
    byId("users-empty").hidden = rows.length !== 0;
    populateUserSelect(byId("binding-user"), "请选择");
    populateUserSelect(byId("binding-filter"), "全部用户");
    populateUserSelect(byId("device-filter"), "全部用户");
  }

  function renderBindings() {
    const filter = byId("binding-filter").value;
    const items = state.bindings.filter((item) => !filter || item.userId === filter);
    const rows = items.map((binding) => {
      const row = document.createElement("tr");
      const statusCell = document.createElement("td");
      statusCell.append(badge(binding.status));
      const buttons = [];
      if (binding.status === "active") {
        buttons.push(actionButton("延长 1 小时", "extend-binding", binding.id));
        buttons.push(actionButton("撤销", "revoke-binding", binding.id, true));
      }
      row.append(
        detailCell(binding.id, "", true),
        detailCell(userName(binding.userId), binding.userId),
        detailCell(binding.deviceTemplate, ""),
        statusCell,
        detailCell(formatTime(binding.expiresAt), ""),
        actionsCell(buttons),
      );
      return row;
    });
    byId("bindings-body").replaceChildren(...rows);
    byId("bindings-empty").hidden = rows.length !== 0;
  }

  function renderDevices() {
    const filter = byId("device-filter").value;
    const items = state.devices.filter((item) => !filter || item.userId === filter);
    const rows = items.map((device) => {
      const row = document.createElement("tr");
      const statusCell = document.createElement("td");
      statusCell.append(badge(device.status));
      const actions = device.status === "active"
        ? [actionButton("禁用", "disable-device", device.id, true)]
        : [];
      row.append(
        detailCell(device.deviceName, device.id),
        detailCell(userName(device.userId), device.userId),
        detailCell(device.platform, device.clientVersion || ""),
        statusCell,
        detailCell(formatTime(device.lastSeenAt), ""),
        actionsCell(actions),
      );
      return row;
    });
    byId("devices-body").replaceChildren(...rows);
    byId("devices-empty").hidden = rows.length !== 0;
  }

  function fillRelease(platform, data) {
    if (!data) return;
    byId(`${platform}-version`).value = data.version || "";
    byId(`${platform}-url`).value = data.downloadUrl || "";
    byId(`${platform}-sha`).value = data.sha256 || "";
    byId(`${platform}-mandatory`).checked = Boolean(data.mandatory);
  }

  function formatBytes(value) {
    const bytes = Number(value) || 0;
    if (bytes < 1024) return `${bytes} B`;
    const units = ["KB", "MB", "GB", "TB"];
    let scaled = bytes / 1024;
    let unit = 0;
    while (scaled >= 1024 && unit < units.length - 1) {
      scaled /= 1024;
      unit += 1;
    }
    return `${scaled.toFixed(1)} ${units[unit]}`;
  }

  function renderReleases() {
    const rows = state.releases.map((release) => {
      const row = document.createElement("tr");
      const statusCell = document.createElement("td");
      statusCell.append(badge(release.status));
      const actions = [];
      if (release.status === "draft") {
        actions.push(actionButton("发布", "publish-release", release.id, false));
      }
      if (release.status !== "revoked") {
        actions.push(actionButton("撤销", "revoke-release", release.id, true));
      }
      row.append(
        detailCell(release.version, release.id),
        detailCell(release.platform, ""),
        detailCell(release.channel, release.mandatory ? "强制" : ""),
        statusCell,
        detailCell(release.artifactSha256 || "", ""),
        actionsCell(actions),
      );
      return row;
    });
    byId("releases-body").replaceChildren(...rows);
    byId("releases-empty").hidden = rows.length !== 0;

    const releaseSelect = byId("assignment-release");
    const current = releaseSelect.value;
    const options = [new Option("（只设开关，不锁版本）", "")];
    for (const release of state.releases) {
      if (release.status !== "published") continue;
      options.push(new Option(`${release.platform} ${release.version}`, release.id));
    }
    releaseSelect.replaceChildren(...options);
    releaseSelect.value = current;
  }

  function renderAssignments() {
    const rows = state.assignments.map((assignment) => {
      const row = document.createElement("tr");
      const release = state.releases.find((item) => item.id === assignment.releaseId);
      const targetLabel = assignment.scope === "platform"
        ? "（全部设备）"
        : assignment.scope === "user"
          ? userName(assignment.targetId)
          : assignment.targetId;
      row.append(
        detailCell(assignment.scope, ""),
        detailCell(targetLabel, assignment.targetId),
        detailCell(assignment.platform, ""),
        detailCell(release ? release.version : "（无）", assignment.releaseId || ""),
        detailCell(assignment.featureFlags || "{}", ""),
        actionsCell([actionButton("删除", "delete-assignment", assignment.id, true)]),
      );
      return row;
    });
    byId("assignments-body").replaceChildren(...rows);
    byId("assignments-empty").hidden = rows.length !== 0;
  }

  function renderAudit() {
    const rows = state.audit.map((entry) => {
      const row = document.createElement("tr");
      row.append(
        detailCell(formatTime(entry.at), ""),
        detailCell(entry.action, ""),
        detailCell(entry.scope || "", ""),
        detailCell(entry.targetId || "", ""),
        detailCell(entry.releaseId || "", entry.previousReleaseId ? `前: ${entry.previousReleaseId}` : ""),
        detailCell(entry.detail || "", ""),
      );
      return row;
    });
    byId("audit-body").replaceChildren(...rows);
    byId("audit-empty").hidden = rows.length !== 0;
  }

  function renderActivity() {
    const rows = state.activity.map((item) => {
      const row = document.createElement("tr");
      const stateCell = document.createElement("td");
      stateCell.append(badge(item.state));
      const location = item.location || {};
      const coarse = [location.city, location.country].filter(Boolean).join(" / ");
      row.dataset.deviceId = item.deviceId;
      row.append(
        detailCell(item.deviceName || item.deviceId, item.deviceId),
        detailCell(userName(item.userId), item.userId),
        stateCell,
        detailCell(formatTime(item.lastSeenAt), ""),
        detailCell(location.ipAddress || "（未知）", location.ipPrefix || "", true),
        detailCell(coarse || "（未填写）", location.isp || ""),
        actionsCell([
          actionButton("最近地址", "show-addresses", item.deviceId, false),
          actionButton("填写位置", "edit-geo", item.deviceId, false),
        ]),
      );
      return row;
    });
    byId("activity-body").replaceChildren(...rows);
    byId("activity-empty").hidden = rows.length !== 0;
  }

  function renderUsage() {
    const usage = state.usage;
    const summary = byId("usage-summary");
    if (!usage) {
      summary.replaceChildren();
      byId("usage-body").replaceChildren();
      byId("usage-empty").hidden = false;
      byId("categories-body").replaceChildren();
      byId("categories-empty").hidden = false;
      return;
    }

    const totals = usage.totals || {};
    summary.replaceChildren(
      ...summaryRow("上行", formatBytes(totals.uplinkBytes)),
      ...summaryRow("下行", formatBytes(totals.downlinkBytes)),
      ...summaryRow("合计", formatBytes(totals.totalBytes)),
      ...summaryRow("保留期", `${usage.retentionDays || 0} 天`),
    );

    const series = Array.isArray(usage.series) ? usage.series : [];
    const rows = series.map((bucket) => {
      const row = document.createElement("tr");
      row.append(
        detailCell(formatTime(bucket.bucketStart), ""),
        detailCell(formatBytes(bucket.uplinkBytes), ""),
        detailCell(formatBytes(bucket.downlinkBytes), ""),
        detailCell(formatBytes(bucket.totalBytes), ""),
      );
      return row;
    });
    byId("usage-body").replaceChildren(...rows);
    // 空桶是照常返回的，所以「没有桶」和「桶里没有流量」是两回事。
    byId("usage-empty").hidden = rows.length !== 0;

    const categories = Array.isArray(usage.categories) ? usage.categories : [];
    const categoryRows = categories.map((item) => {
      const row = document.createElement("tr");
      row.append(
        detailCell(item.category, ""),
        detailCell(formatBytes(item.totalBytes), ""),
      );
      return row;
    });
    byId("categories-body").replaceChildren(...categoryRows);
    byId("categories-empty").hidden = categoryRows.length !== 0;
  }

  function summaryRow(label, value) {
    const dt = document.createElement("dt");
    dt.textContent = label;
    const dd = document.createElement("dd");
    dd.textContent = value;
    return [dt, dd];
  }

  function releaseSigning() {
    if (!window.isSecureContext || !window.crypto || !window.crypto.subtle) {
      throw new Error("本地签名需要安全浏览器环境。请用 SSH 启动器打开 localhost，或使用可信 HTTPS 地址。");
    }
    if (!window.MyProxyReleaseSigning) throw new Error("发布签名组件未加载，请刷新页面。");
    return window.MyProxyReleaseSigning;
  }

  function wizardTrusted() {
    const settings = state.releaseSettings;
    const keyId = wizard.snapshot ? wizard.snapshot.signingKeyId : byId("wizard-key-id").value.trim();
    return Boolean(settings && wizard.key && Array.isArray(settings.trustedSigningKeys) &&
      settings.trustedSigningKeys.some((entry) => entry.keyId === keyId && entry.publicKeyHex === wizard.key.publicKeyHex));
  }

  function updateWizardControls() {
    const secure = Boolean(window.isSecureContext && window.crypto && window.crypto.subtle && window.MyProxyReleaseSigning);
    const settings = state.releaseSettings;
    const enabled = Boolean(settings && settings.artifactUploadEnabled);
    text(byId("wizard-environment"), !secure
      ? "本地签名需要安全浏览器环境。请通过 SSH 启动器打开 localhost，或使用可信 HTTPS 地址。"
      : !settings ? "正在读取发布存储与可信公钥配置…"
        : enabled ? `发布存储已就绪 · 单个文件上限 ${formatBytes(settings.maxArtifactBytes)}。私钥只在当前浏览器中使用。`
          : "此服务器未启用安装包上传。可填写外部 HTTPS 下载地址，签署并导出清单，或由管理员启用发布存储。");
    const platform = byId("wizard-platform").value;
    byId("wizard-signer-field").hidden = platform === "linux";
    byId("wizard-subject").required = platform !== "linux";
    text(byId("wizard-signer-help"), platform === "android"
      ? "填写 apksigner 输出的 APK 签名证书 SHA-256；更新须沿用原应用签名。"
      : "填写 ZIP 根目录 MyProxy.exe 的 Authenticode 签名证书 SHA-256；请先完成安装包签名。");
    byId("wizard-external-url").disabled = wizard.busy || Boolean(wizard.release) || byId("wizard-managed").checked;
    byId("wizard-external-url").required = !byId("wizard-managed").checked;
    for (const input of byId("release-wizard-form").elements) {
      if (input.id === "wizard-reset") input.disabled = wizard.busy;
      else if (input.id !== "wizard-external-url" && input.id !== "wizard-backup-key") {
        input.disabled = wizard.busy || Boolean(wizard.release);
      }
    }
    byId("wizard-prepare").disabled = wizard.busy || Boolean(wizard.release) || !secure || !wizard.key;
    byId("wizard-generate-key").disabled = wizard.busy || Boolean(wizard.release) || !secure;
    byId("wizard-key-file").disabled = wizard.busy || Boolean(wizard.release) || !secure;
    byId("wizard-backup-key").disabled = wizard.busy || !wizard.key || !wizard.key.privateKeyPem;
    byId("wizard-export").disabled = wizard.busy || !wizard.prepared;
    const complete = wizard.published && (!wizard.snapshot || !wizard.snapshot.assignment || wizard.assigned);
    byId("wizard-publish").disabled = wizard.busy || !wizard.prepared || !wizardTrusted() ||
      (wizard.snapshot && wizard.snapshot.managed && !enabled) || complete;
    text(byId("wizard-publish"), wizard.busy ? "正在处理…" : wizard.release
      ? complete ? "已完成发布" : "继续发布 / 重试" : wizard.snapshot && !wizard.snapshot.managed ? "登记并发布" : "上传并发布");
    if (wizard.key) {
      text(byId("wizard-key-status"), wizardTrusted()
        ? "公钥与服务端可信配置一致。私钥只在当前浏览器内存中，刷新或退出会清除。"
        : "服务端尚未信任此 keyId 与公钥。请先配置公钥并刷新；可以先导出已签名清单。");
    }
  }

  function refreshWizardTargets() {
    if (wizard.release || wizard.busy) return;
    const select = byId("wizard-target");
    const previous = select.value;
    const scope = byId("wizard-scope").value;
    const platform = byId("wizard-platform").value;
    const options = [new Option(scope === "none" || scope === "platform" ? "无需目标" : "请选择目标", "")];
    if (scope === "user") {
      for (const user of state.users) {
        if (user.status === "active") options.push(new Option(user.displayName || user.username, user.id));
      }
    } else if (scope === "device") {
      for (const device of state.devices) {
        if (device.status === "active" && device.platform === platform) {
          options.push(new Option(`${device.deviceName} · ${userName(device.userId)}`, device.id));
        }
      }
    }
    select.replaceChildren(...options);
    if (options.some((option) => option.value === previous)) select.value = previous;
    select.required = scope === "user" || scope === "device";
  }

  function clearWizard(clearKey = false) {
    wizard.revision += 1;
    wizard.prepared = null;
    wizard.snapshot = null;
    wizard.release = null;
    wizard.uploaded = false;
    wizard.signatureUploaded = false;
    wizard.published = false;
    wizard.assigned = false;
    wizard.appliedFlags = null;
    wizard.flagsChanged = false;
    wizard.busy = false;
    byId("wizard-review").hidden = true;
    byId("wizard-summary").replaceChildren();
    text(byId("wizard-manifest"), "");
    text(byId("wizard-progress"), "");
    if (clearKey) {
      wizard.key = null;
      byId("wizard-key-file").value = "";
      byId("wizard-file").value = "";
      byId("wizard-public-key").value = "";
      byId("wizard-key-config").value = "";
      byId("wizard-linux-key-config").value = "";
      text(byId("wizard-key-status"), "私钥只留在当前浏览器内存；不会上传。刷新或退出会清除它。");
    }
    refreshWizardTargets();
    updateWizardControls();
  }

  function invalidateWizard() {
    if (wizard.busy || wizard.release) return;
    clearWizard();
    byId("wizard-key-config").value = wizard.key
      ? `${byId("wizard-key-id").value.trim()}:${wizard.key.publicKeyHex}` : "";
  }

  async function selectWizardKey(action) {
    if (wizard.busy || wizard.release) return;
    const helper = releaseSigning();
    clearWizard();
    wizard.key = null;
    byId("wizard-public-key").value = "";
    byId("wizard-key-config").value = "";
    byId("wizard-linux-key-config").value = "";
    const revision = wizard.revision;
    wizard.busy = true;
    updateWizardControls();
    text(byId("wizard-key-status"), "正在本地读取发布密钥…");
    try {
      const key = await action(helper);
      const bytes = new Uint8Array(key.publicKeyHex.match(/../g).map((byte) => parseInt(byte, 16)));
      const digest = await window.crypto.subtle.digest("SHA-256", bytes);
      const subject = [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
      if (revision !== wizard.revision) return;
      wizard.key = key;
      if (key.privateKeyPem) byId("wizard-key-file").value = "";
      byId("wizard-public-key").value = key.publicKeyHex;
      byId("wizard-key-config").value = `${byId("wizard-key-id").value.trim()}:${key.publicKeyHex}`;
      byId("wizard-linux-key-config").value = `${subject}:${key.publicKeyHex}`;
      showPageMessage(key.privateKeyPem ? "密钥已在本地生成，请下载私钥备份。" : "发布密钥已在本地读取。");
    } catch (error) {
      text(byId("wizard-key-status"), `密钥未加载。${errorMessage(error)}`);
      throw error;
    } finally {
      if (revision === wizard.revision) { wizard.busy = false; updateWizardControls(); }
    }
  }

  function saveDownload(name, data, type) {
    const url = URL.createObjectURL(new Blob([data], { type }));
    const link = document.createElement("a");
    link.href = url;
    link.download = name;
    document.body.append(link);
    link.click();
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  function existingAssignmentFlags(assignments, assignment) {
    const existing = assignments.find((item) => item.scope === assignment.scope &&
      (item.targetId || "") === assignment.targetId && item.platform === assignment.platform);
    if (!existing || existing.featureFlags == null || existing.featureFlags === "") return {};
    const flags = typeof existing.featureFlags === "string"
      ? JSON.parse(existing.featureFlags) : existing.featureFlags;
    if (!flags || typeof flags !== "object" || Array.isArray(flags)) {
      throw new Error("现有功能开关格式无效，未保存指派。");
    }
    return { ...flags };
  }

  async function prepareWizard() {
    if (wizard.busy || wizard.release) return;
    const form = byId("release-wizard-form");
    if (!form.reportValidity()) return;
    const helper = releaseSigning();
    if (!wizard.key) throw new Error("请先选择私钥或生成本地发布密钥。");
    const file = byId("wizard-file").files[0];
    if (!file || !file.size) throw new Error("请选择非空安装包。");
    const platform = byId("wizard-platform").value;
    const extensions = { windows: /\.zip$/i, android: /\.apk$/i, linux: /\.(tar\.gz|tgz)$/i };
    if (!extensions[platform].test(file.name)) throw new Error("安装包扩展名与所选平台不一致。");
    const scope = byId("wizard-scope").value;
    const targetId = byId("wizard-target").value;
    if ((scope === "user" || scope === "device") && !targetId) throw new Error("请选择指派目标。");
    const managed = byId("wizard-managed").checked;
    const settings = state.releaseSettings;
    if (managed && (!settings || !settings.artifactUploadEnabled)) throw new Error("服务器尚未启用上传；可取消上传并填写外部下载地址。");
    if (managed && file.size > settings.maxArtifactBytes) throw new Error("安装包超过服务器允许的文件大小。");
    const snapshot = {
      file, platform, managed, version: byId("wizard-version").value.trim(),
      channel: byId("wizard-channel").value, mandatory: byId("wizard-mandatory").checked,
      signingKeyId: byId("wizard-key-id").value.trim(),
      minimumVersion: byId("wizard-minimum").value.trim(),
      subjectSha256: byId("wizard-subject").value.trim().toLowerCase(),
      note: byId("wizard-note").value,
      assignment: scope === "none" ? null : { scope, targetId: scope === "platform" ? "" : targetId, platform },
    };
    if (snapshot.assignment) {
      snapshot.assignment.featureFlags = existingAssignmentFlags(state.assignments, snapshot.assignment);
    }
    const revision = ++wizard.revision;
    wizard.busy = true;
    updateWizardControls();
    showPageMessage("正在本地计算 SHA-256 并签署清单…");
    try {
      const hash = await helper.hashFile(file);
      const extension = managed ? settings.artifactExtensions[platform] : "";
      const artifactUrl = managed
        ? `${settings.artifactBaseUrl.replace(/\/$/, "")}/${hash}/myproxy-${platform}-${snapshot.version}.${extension}`
        : byId("wizard-external-url").value.trim();
      const prepared = await helper.prepare({
        key: wizard.key.key, signingKeyId: snapshot.signingKeyId, file,
        platform, version: snapshot.version, channel: snapshot.channel, mandatory: snapshot.mandatory,
        minimumVersion: snapshot.minimumVersion, subjectSha256: snapshot.subjectSha256, artifactUrl,
      });
      if (revision !== wizard.revision) return;
      wizard.prepared = prepared;
      wizard.snapshot = snapshot;
      byId("wizard-review").hidden = false;
      const targetLabel = !snapshot.assignment ? "仅发布，暂不指派" : scope === "platform"
        ? `${platform} 全部设备` : scope === "user" ? `用户：${userName(targetId)}`
          : `设备：${byId("wizard-target").selectedOptions[0].textContent} (${targetId})`;
      const rows = [
        ["版本 / 平台 / 通道", `${snapshot.version} / ${platform} / ${snapshot.channel}`],
        ["安装包", `${file.name} · ${formatBytes(prepared.artifactSize)} (${prepared.artifactSize} 字节)`],
        ["SHA-256", prepared.artifactSha256], ["下载地址", artifactUrl],
        ["签名 keyId", snapshot.signingKeyId], ["公钥", wizard.key.publicKeyHex],
        ["更新要求", snapshot.mandatory ? "必须更新" : "可选更新"],
        ["指派目标", targetLabel],
      ];
      if (snapshot.assignment) rows.push(["保留现有功能开关", JSON.stringify(snapshot.assignment.featureFlags)]);
      byId("wizard-summary").replaceChildren(...rows.map(([label, value]) => {
        const row = element("div", "summary-row");
        row.append(element("dt", "", label), element("dd", "", value));
        return row;
      }));
      text(byId("wizard-manifest"), JSON.stringify(prepared.manifestDocument, null, 2));
      text(byId("wizard-progress"), "清单已在本地签署，尚未上传或登记。请核对上述版本、文件与目标，再点击发布；也可先导出清单。");
      showPageMessage("发布摘要已就绪");
    } finally {
      if (revision === wizard.revision) { wizard.busy = false; updateWizardControls(); }
    }
  }

  function sameWizardRelease(release) {
    const snapshot = wizard.snapshot;
    const document = wizard.prepared.manifestDocument;
    return release.platform === snapshot.platform && release.version === snapshot.version &&
      release.channel === snapshot.channel && release.artifactSha256 === wizard.prepared.artifactSha256 &&
      release.artifactSize === wizard.prepared.artifactSize && release.artifactUrl === document.artifact.url &&
      release.signingKeyId === snapshot.signingKeyId && release.mandatory === snapshot.mandatory &&
      (release.minimumVersion || "") === snapshot.minimumVersion &&
      release.platformSignatureSubjectSha256 === document.artifact.signature.subjectSha256;
  }

  async function synchronizeWizardRelease() {
    const snapshot = wizard.snapshot;
    const listing = await api(`/api/admin/release?platform=${encodeURIComponent(snapshot.platform)}`);
    if (snapshot !== wizard.snapshot) return false;
    const existing = (listing.releases || []).find((release) => release.version === wizard.snapshot.version);
    if (!existing) return false;
    if (!sameWizardRelease(existing) || existing.status === "revoked") {
      throw new Error("此平台版本已登记为另一份清单或已撤销。请使用新版本号，不能覆盖既有发布。");
    }
    wizard.release = existing;
    wizard.uploaded = Boolean(existing.artifactReady);
    wizard.signatureUploaded = Boolean(existing.platformSignatureReady);
    wizard.published = existing.status === "published";
    return true;
  }

  async function publishWizard() {
    if (wizard.busy || !wizard.prepared || !wizard.snapshot) return;
    if (!wizardTrusted()) throw new Error("服务端尚未信任当前签名公钥，请完成公钥配置并刷新。");
    if (wizard.snapshot.managed && !state.releaseSettings.artifactUploadEnabled) throw new Error("服务器未启用安装包上传。");
    const revision = wizard.revision;
    const progress = (value) => { if (revision === wizard.revision) text(byId("wizard-progress"), value); };
    wizard.busy = true;
    updateWizardControls();
    try {
      progress("正在核对并登记草稿…");
      if (!wizard.release) {
        const found = await synchronizeWizardRelease();
        if (revision !== wizard.revision) return;
        if (!found) {
          const prepared = wizard.prepared;
          try {
            const created = await api("/api/admin/release", { method: "POST", body: {
              manifest: prepared.manifest, signature: prepared.signature,
              signingKeyId: prepared.signingKeyId, note: wizard.snapshot.note,
            } });
            if (revision !== wizard.revision) return;
            wizard.release = created;
          } catch (error) {
            // Registration may have succeeded before a connection was lost.
            // Re-query the immutable version before offering a safe retry.
            if (!await synchronizeWizardRelease()) throw error;
          }
        }
      } else {
        await synchronizeWizardRelease();
      }
      if (revision !== wizard.revision) return;
      const id = encodeURIComponent(wizard.release.id);
      if (!wizard.published && wizard.snapshot.managed) {
        if (!wizard.uploaded) {
          progress(`草稿 ${wizard.release.id} 已登记，正在上传安装包…`);
          const uploaded = await api(`/api/admin/release/${id}/artifact`, { method: "POST", rawBody: wizard.snapshot.file });
          if (revision !== wizard.revision) return;
          wizard.uploaded = Boolean(uploaded.artifactReady);
          if (!wizard.uploaded) throw new Error("服务器未确认安装包就绪，请重试。");
        }
        if (wizard.snapshot.platform === "linux" && !wizard.signatureUploaded) {
          progress("安装包已上传，正在上传 Linux 产物签名…");
          const signed = await api(`/api/admin/release/${id}/artifact-signature`, {
            method: "POST", rawBody: releaseSigning().decodeBase64(wizard.prepared.artifactSignature),
          });
          if (revision !== wizard.revision) return;
          wizard.signatureUploaded = Boolean(signed.platformSignatureReady);
          if (!wizard.signatureUploaded) throw new Error("服务器未确认 Linux 产物签名就绪，请重试。");
        }
      }
      if (revision !== wizard.revision) return;
      if (!wizard.published) {
        progress("安装包已就绪，正在发布版本…");
        await api(`/api/admin/release/${id}/publish`, { method: "POST" });
        if (revision !== wizard.revision) return;
        wizard.published = true;
      }
      if (revision !== wizard.revision) return;
      if (wizard.snapshot.assignment && !wizard.assigned) {
        progress("版本已发布，正在保存指派…");
        const assignments = await api("/api/admin/assignment");
        if (revision !== wizard.revision) return;
        // The wizard changes the version pin only. Re-read the current switches
        // just before saving so a separate settings edit is not overwritten.
        const latestFlags = existingAssignmentFlags(assignments.assignments || [], wizard.snapshot.assignment);
        await api("/api/admin/assignment", { method: "POST", body: {
          ...wizard.snapshot.assignment, featureFlags: latestFlags,
          releaseId: wizard.release.id, note: wizard.snapshot.note,
        } });
        if (revision !== wizard.revision) return;
        wizard.assigned = true;
        wizard.appliedFlags = latestFlags;
        wizard.flagsChanged = JSON.stringify(latestFlags) !== JSON.stringify(wizard.snapshot.assignment.featureFlags);
      }
      progress(`版本 ${wizard.snapshot.version} 已发布${wizard.assigned ? "并完成指派" : "，暂未指派"}。release：${wizard.release.id}` +
        (wizard.flagsChanged ? `。指派保留了保存时最新的功能开关：${JSON.stringify(wizard.appliedFlags)}` : ""));
      await refreshAll();
      showToast("发布已完成");
    } catch (error) {
      const stage = wizard.published ? "版本已发布" : wizard.release ? `草稿 ${wizard.release.id} 已登记` : "草稿状态尚未确认";
      progress(`${stage}。${errorMessage(error)} 已完成的阶段会保留，点击「继续发布 / 重试」可继续。`);
      throw error;
    } finally {
      if (revision === wizard.revision) { wizard.busy = false; updateWizardControls(); }
    }
  }

  function refreshAssignmentTargets() {
    const scope = byId("assignment-scope").value;
    const select = byId("assignment-target");
    const options = [];
    if (scope === "platform") {
      options.push(new Option("（平台默认无需目标）", ""));
    } else if (scope === "user") {
      for (const user of state.users) options.push(new Option(user.displayName || user.username, user.id));
    } else {
      for (const device of state.devices) {
        if (device.status !== "active") continue;
        options.push(new Option(`${device.deviceName} (${device.platform})`, device.id));
      }
    }
    select.replaceChildren(...options);
  }

  function refreshUsageDevices() {
    const select = byId("usage-device");
    const current = select.value;
    const options = [new Option("全部设备", "")];
    for (const device of state.devices) {
      options.push(new Option(`${device.deviceName} (${device.platform})`, device.id));
    }
    select.replaceChildren(...options);
    select.value = current;
  }

  function renderAll() {
    renderOverview();
    renderUsers();
    renderBindings();
    renderDevices();
    fillRelease("windows", state.latest.windows);
    fillRelease("android", state.latest.android);
    fillRelease("linux", state.latest.linux);
    renderReleases();
    renderAssignments();
    renderAudit();
    renderActivity();
    renderUsage();
    refreshAssignmentTargets();
    refreshUsageDevices();
    refreshWizardTargets();
    updateWizardControls();
  }

  function setView(name) {
    if (!views.includes(name)) return;
    state.activeView = name;
    text(byId("workspace-title"), byId(`${name}-section`).querySelector("h2").textContent);
    for (const view of views) byId(`${view}-section`).hidden = view !== name;
    for (const button of document.querySelectorAll(".nav-button")) {
      const active = button.dataset.view === name;
      button.classList.toggle("is-active", active);
      button.setAttribute("aria-pressed", active ? "true" : "false");
    }
    showPageMessage("");
  }

  async function refreshAll() {
    showPageMessage("正在刷新…");
    serviceState.className = "status";
    text(serviceState, "正在检查");
    const [
      health, users, bindings, devices, config, windows, android, linux,
      releases, assignments, audit, activity, releaseSettings,
    ] = await Promise.all([
      api("/healthz"),
      api("/api/admin/user"),
      api("/api/admin/binding"),
      api("/api/admin/device"),
      api("/api/admin/config"),
      api("/api/admin/latest"),
      api("/api/admin/latest/android"),
      api("/api/admin/latest/linux"),
      api("/api/admin/release"),
      api("/api/admin/assignment"),
      api("/api/admin/release-audit?limit=50"),
      api("/api/admin/activity"),
      api("/api/admin/release-settings"),
    ]);
    state.releases = Array.isArray(releases.releases) ? releases.releases : [];
    state.releaseSettings = releaseSettings;
    state.assignments = Array.isArray(assignments.assignments) ? assignments.assignments : [];
    state.audit = Array.isArray(audit.entries) ? audit.entries : [];
    state.activity = Array.isArray(activity.devices) ? activity.devices : [];
    state.health = health;
    state.users = Array.isArray(users.users) ? users.users : [];
    state.bindings = Array.isArray(bindings.bindings) ? bindings.bindings : [];
    state.devices = Array.isArray(devices.devices) ? devices.devices : [];
    state.config = config;
    state.latest.windows = windows;
    state.latest.android = android;
    state.latest.linux = linux;
    renderAll();
    serviceState.className = "status is-online";
    text(serviceState, "服务正常");
    showPageMessage(`已刷新 · ${new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false }).format(new Date())}`);
  }

  async function refreshAfterAction(successMessage) {
    await refreshAll();
    showToast(successMessage);
  }

  async function enterFromSsh() {
    let ticket = new URLSearchParams(window.location.hash.slice(1)).get("ticket") || "";
    // Remove the single-use ticket before any network request or rendering.
    window.history.replaceState(null, "", window.location.pathname);
    if (!ticket) {
      lock("请从 SSH 密钥启动器进入。");
      return;
    }
    text(loginError, "正在建立会话…");
    try {
      const session = await api("/admin/session", { method: "POST", body: { ticket } });
      state.token = session.token;
      ticket = "";
      await refreshAll();
      loginView.hidden = true;
      appView.hidden = false;
      setView("overview");
    } catch (error) {
      lock("无法建立会话，请重新运行「打开管理后台.cmd」。");
    } finally {
      ticket = "";
    }
  }

  byId("logout-button").addEventListener("click", async (event) => {
    const button = event.currentTarget;
    setBusy(button, true, "退出中…");
    try {
      await api("/api/admin/ui-session", { method: "DELETE" });
      lock("已退出，请重新从 SSH 密钥启动器进入。");
    } catch (error) {
      // Keep the session so the user can retry revocation after a network error.
      showPageMessage("退出失败，请重试或关闭 SSH 隧道。", true);
    } finally { setBusy(button, false, ""); }
  });
  byId("refresh-button").addEventListener("click", async (event) => {
    const button = event.currentTarget;
    setBusy(button, true, "刷新中…");
    try { await refreshAll(); }
    catch (error) { showPageMessage(errorMessage(error), true); }
    finally { setBusy(button, false, ""); }
  });

  document.querySelector(".section-nav").addEventListener("click", (event) => {
    const button = event.target.closest("button[data-view]");
    if (button) setView(button.dataset.view);
  });

  byId("user-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const button = form.querySelector("button[type=submit]");
    setBusy(button, true, "创建中…");
    try {
      await api("/api/admin/user", {
        method: "POST",
        body: { username: byId("username-input").value, displayName: byId("display-name-input").value },
      });
      form.reset();
      await refreshAfterAction("用户已创建");
    } catch (error) { showPageMessage(errorMessage(error), true); }
    finally { setBusy(button, false, ""); }
  });

  byId("binding-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const button = event.currentTarget.querySelector("button[type=submit]");
    setBusy(button, true, "生成中…");
    try {
      const result = await api("/api/admin/binding", {
        method: "POST",
        body: {
          userId: byId("binding-user").value,
          deviceTemplate: byId("device-template").value,
          expiresInSeconds: Number(byId("binding-ttl").value),
        },
      });
      state.newBindingCode = result.code || "";
      text(byId("new-binding-code"), state.newBindingCode);
      byId("new-binding-result").hidden = !state.newBindingCode;
      showToast("配对码已生成，请立即复制");
      await refreshAll();
    } catch (error) { showPageMessage(errorMessage(error), true); }
    finally { setBusy(button, false, ""); }
  });

  byId("users-body").addEventListener("click", async (event) => {
    const button = event.target.closest("button[data-action]");
    if (!button) return;
    const user = state.users.find((item) => item.id === button.dataset.id);
    if (!user) return;
    if (button.dataset.action === "issue-binding") {
      byId("binding-user").value = user.id;
      setView("bindings");
      byId("device-template").focus();
      return;
    }
    if (button.dataset.action === "delete-user") {
      if (!window.confirm(`删除用户“${user.displayName || user.username}”并禁用其设备？`)) return;
      setBusy(button, true, "删除中…");
      try {
        await api(`/api/admin/user/${encodeURIComponent(user.id)}`, { method: "DELETE" });
        await refreshAfterAction("用户已删除");
      } catch (error) { showPageMessage(errorMessage(error), true); }
      finally { setBusy(button, false, ""); }
    }
  });

  byId("bindings-body").addEventListener("click", async (event) => {
    const button = event.target.closest("button[data-action]");
    if (!button) return;
    const binding = state.bindings.find((item) => item.id === button.dataset.id);
    if (!binding) return;
    if (button.dataset.action === "revoke-binding" && !window.confirm(`撤销配对记录 ${binding.id}？`)) return;
    setBusy(button, true, button.dataset.action === "extend-binding" ? "延期中…" : "撤销中…");
    try {
      if (button.dataset.action === "extend-binding") {
        await api(`/api/admin/binding/${encodeURIComponent(binding.id)}/extend`, {
          method: "POST", body: { extendsSeconds: 3600 },
        });
        await refreshAfterAction("配对码已延长 1 小时");
      } else if (button.dataset.action === "revoke-binding") {
        await api(`/api/admin/binding/${encodeURIComponent(binding.id)}`, { method: "DELETE" });
        await refreshAfterAction("配对码已撤销");
      }
    } catch (error) { showPageMessage(errorMessage(error), true); }
    finally { setBusy(button, false, ""); }
  });

  byId("devices-body").addEventListener("click", async (event) => {
    const button = event.target.closest("button[data-action=disable-device]");
    if (!button) return;
    const device = state.devices.find((item) => item.id === button.dataset.id);
    if (!device || !window.confirm(`禁用设备“${device.deviceName}”？`)) return;
    setBusy(button, true, "禁用中…");
    try {
      await api(`/api/admin/device/${encodeURIComponent(device.id)}`, { method: "DELETE" });
      await refreshAfterAction("设备已禁用");
    } catch (error) { showPageMessage(errorMessage(error), true); }
    finally { setBusy(button, false, ""); }
  });

  byId("binding-filter").addEventListener("change", renderBindings);
  byId("device-filter").addEventListener("change", renderDevices);

  byId("assignment-scope").addEventListener("change", refreshAssignmentTargets);

  byId("release-wizard-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    try { await prepareWizard(); }
    catch (error) { showPageMessage(errorMessage(error), true); }
  });
  byId("release-wizard-form").addEventListener("input", (event) => {
    if (event.target.id !== "wizard-key-file") invalidateWizard();
  });
  for (const id of ["wizard-platform", "wizard-scope", "wizard-managed"]) {
    byId(id).addEventListener("change", () => {
      invalidateWizard();
      refreshWizardTargets();
      updateWizardControls();
    });
  }
  byId("wizard-key-file").addEventListener("change", async () => {
    const file = byId("wizard-key-file").files[0];
    if (!file) return;
    try { await selectWizardKey((helper) => helper.loadKey(file)); }
    catch (error) { showPageMessage(errorMessage(error), true); }
  });
  byId("wizard-generate-key").addEventListener("click", async () => {
    try { await selectWizardKey((helper) => helper.generateKey()); }
    catch (error) { showPageMessage(errorMessage(error), true); }
  });
  byId("wizard-backup-key").addEventListener("click", () => {
    if (!wizard.key || !wizard.key.privateKeyPem) return;
    saveDownload("myproxy-release-private-key.pem", wizard.key.privateKeyPem, "application/x-pem-file");
    showToast("私钥备份已下载，请妥善保存。");
  });
  for (const [buttonId, fieldId] of [["wizard-copy-key", "wizard-key-config"], ["wizard-copy-linux-key", "wizard-linux-key-config"]]) {
    byId(buttonId).addEventListener("click", async () => {
      const value = byId(fieldId).value;
      if (!value) return;
      try { await navigator.clipboard.writeText(value); showToast("公钥配置已复制"); }
      catch (_) { showToast("复制不可用，请选择公钥配置文本后手动复制。"); }
    });
  }
  byId("wizard-reset").addEventListener("click", () => {
    if (wizard.busy) return;
    const previous = wizard.release;
    clearWizard();
    showPageMessage(previous ? `已有 release ${previous.id} 的状态已保留。请选择新版本和安装包。` : "可以重新选择版本和安装包。");
  });
  byId("wizard-export").addEventListener("click", () => {
    if (!wizard.prepared) return;
    saveDownload(`myproxy-${wizard.snapshot.platform}-${wizard.snapshot.version}-manifest.json`,
      releaseSigning().exportEnvelope(wizard.prepared), "application/json");
    if (wizard.prepared.artifactSignature) {
      const artifactName = new URL(wizard.prepared.manifestDocument.artifact.url).pathname.split("/").pop()
        || `myproxy-linux-${wizard.snapshot.version}.tar.gz`;
      saveDownload(`${artifactName}.sig`, wizard.prepared.artifactSignature + "\n", "text/plain");
    }
    showToast("签名清单已导出；Linux 产物签名也会单独下载。");
  });
  byId("wizard-publish").addEventListener("click", async () => {
    try { await publishWizard(); }
    catch (error) { showPageMessage(errorMessage(error), true); }
  });
  updateWizardControls();

  byId("release-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const button = form.querySelector("button[type=submit]");
    setBusy(button, true, "登记中…");
    try {
      await api("/api/admin/release", {
        method: "POST",
        body: {
          manifest: byId("release-manifest").value.trim(),
          signature: byId("release-signature").value.trim(),
          signingKeyId: byId("release-key-id").value.trim(),
          note: byId("release-note").value,
        },
      });
      form.reset();
      await refreshAll();
      showToast("release 已登记，状态为 draft");
    } catch (error) {
      showPageMessage(errorMessage(error), true);
    } finally {
      setBusy(button, false, "");
    }
  });

  byId("assignment-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const button = form.querySelector("button[type=submit]");
    const rawFlags = byId("assignment-flags").value.trim();
    let featureFlags;
    if (rawFlags) {
      try {
        featureFlags = JSON.parse(rawFlags);
      } catch (_) {
        showPageMessage("feature flags 必须是合法的 JSON 对象", true);
        return;
      }
    }
    setBusy(button, true, "保存中…");
    try {
      const body = {
        scope: byId("assignment-scope").value,
        targetId: byId("assignment-target").value,
        platform: byId("assignment-platform").value,
      };
      const releaseId = byId("assignment-release").value;
      if (releaseId) body.releaseId = releaseId;
      if (featureFlags) body.featureFlags = featureFlags;
      await api("/api/admin/assignment", { method: "POST", body });
      await refreshAll();
      showToast("指派已保存");
    } catch (error) {
      showPageMessage(errorMessage(error), true);
    } finally {
      setBusy(button, false, "");
    }
  });

  byId("releases-body").addEventListener("click", async (event) => {
    const button = event.target.closest("button[data-action]");
    if (!button) return;
    const { action, id } = button.dataset;
    if (action === "publish-release") {
      await runAction(button, "发布中…", `/api/admin/release/${id}/publish`, "POST", "release 已发布");
    } else if (action === "revoke-release") {
      // 撤销会让指向它的指派向下一层回落，也就是一次全量回滚。
      if (!confirm("撤销后指向它的指派会回落到上一层，确认？")) return;
      await runAction(button, "撤销中…", `/api/admin/release/${id}/revoke`, "POST", "release 已撤销");
    }
  });

  byId("assignments-body").addEventListener("click", async (event) => {
    const button = event.target.closest("button[data-action=delete-assignment]");
    if (!button) return;
    if (!confirm("删除这条指派？")) return;
    await runAction(button, "删除中…", `/api/admin/assignment/${button.dataset.id}`, "DELETE", "指派已删除");
  });

  byId("activity-body").addEventListener("click", async (event) => {
    const button = event.target.closest("button[data-action]");
    if (!button) return;

    if (button.dataset.action === "show-addresses") {
      const row = button.closest("tr");
      const next = row.nextElementSibling;
      // Second click closes it, so an operator can open several devices in
      // turn without the table filling up with panels they cannot dismiss.
      if (next && next.classList.contains("address-detail")) {
        next.remove();
        return;
      }
      setBusy(button, true, "读取中…");
      try {
        const detail = await api(`/api/admin/device/${button.dataset.id}/activity`);
        const entries = Array.isArray(detail.recentAddresses) ? detail.recentAddresses : [];
        const detailRow = document.createElement("tr");
        detailRow.className = "address-detail";
        const cell = document.createElement("td");
        cell.colSpan = 7;
        if (entries.length === 0) {
          cell.append(element("div", "secondary-line", "还没有记录到地址。"));
        } else {
          for (const entry of entries) {
            cell.append(element(
              "div",
              "secondary-line mono",
              `${entry.address} · 首次 ${formatTime(entry.firstSeenAt)} · 最近 ${formatTime(entry.lastSeenAt)}`,
            ));
          }
          cell.append(element(
            "div",
            "secondary-line",
            `最多保留最近 ${detail.addressHistoryLimit} 个不同地址。`,
          ));
        }
        detailRow.append(cell);
        row.after(detailRow);
      } catch (error) {
        showPageMessage(errorMessage(error), true);
      } finally {
        setBusy(button, false, "");
      }
      return;
    }

    if (button.dataset.action !== "edit-geo") return;
    const country = prompt("国家/地区（留空表示清除）");
    if (country === null) return;
    const city = prompt("城市（留空表示清除）");
    if (city === null) return;
    setBusy(button, true, "保存中…");
    try {
      await api(`/api/admin/device/${button.dataset.id}/geo`, {
        method: "POST",
        body: { country, city },
      });
      await refreshAll();
      showToast("位置已保存");
    } catch (error) {
      showPageMessage(errorMessage(error), true);
    } finally {
      setBusy(button, false, "");
    }
  });

  byId("usage-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const button = event.currentTarget.querySelector("button[type=submit]");
    setBusy(button, true, "查询中…");
    try {
      const params = new URLSearchParams();
      const deviceId = byId("usage-device").value;
      if (deviceId) params.set("deviceId", deviceId);
      params.set("granularity", byId("usage-granularity").value);
      state.usage = await api(`/api/admin/usage?${params.toString()}`);
      renderUsage();
      showPageMessage("");
    } catch (error) {
      showPageMessage(errorMessage(error), true);
    } finally {
      setBusy(button, false, "");
    }
  });

  async function runAction(button, busyLabel, path, method, successMessage) {
    setBusy(button, true, busyLabel);
    try {
      await api(path, { method });
      await refreshAll();
      showToast(successMessage);
    } catch (error) {
      showPageMessage(errorMessage(error), true);
    } finally {
      setBusy(button, false, "");
    }
  }
  byId("copy-new-binding").addEventListener("click", async () => {
    if (!state.newBindingCode) return;
    try {
      await navigator.clipboard.writeText(state.newBindingCode);
      showToast("配对码已复制");
    } catch (_) {
      showPageMessage("浏览器未允许复制，请手动选择新配对码", true);
    }
  });
  byId("dismiss-new-binding").addEventListener("click", () => {
    state.newBindingCode = "";
    text(byId("new-binding-code"), "");
    byId("new-binding-result").hidden = true;
  });

  byId("config-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    if (!window.confirm("从当前 3x-ui 配置生成新的配置版本？")) return;
    const form = event.currentTarget;
    const button = form.querySelector("button[type=submit]");
    setBusy(button, true, "处理中…");
    try {
      await api("/api/admin/config/version", {
        method: "POST", body: { note: byId("config-note").value },
      });
      form.reset();
      await refreshAfterAction("配置版本已提升");
    } catch (error) { showPageMessage(errorMessage(error), true); }
    finally { setBusy(button, false, ""); }
  });

  for (const formId of ["windows-release-form", "android-release-form", "linux-release-form"]) {
    byId(formId).addEventListener("submit", async (event) => {
      event.preventDefault();
      const platform = event.currentTarget.dataset.platform;
      if (!window.confirm(`保存 ${{ windows: "Windows", android: "Android", linux: "Linux" }[platform] || platform} 发布信息？`)) return;
      const button = event.currentTarget.querySelector("button[type=submit]");
      setBusy(button, true, "保存中…");
      try {
        const suffix = platform === "windows" ? "" : `/${{platform}}`;
        await api(`/api/admin/latest${suffix}`, {
          method: "POST",
          body: {
            version: byId(`${platform}-version`).value,
            downloadUrl: byId(`${platform}-url`).value,
            sha256: byId(`${platform}-sha`).value,
            mandatory: byId(`${platform}-mandatory`).checked,
          },
        });
        await refreshAfterAction(`${platform === "windows" ? "Windows" : "Android"} 发布信息已保存`);
      } catch (error) { showPageMessage(errorMessage(error), true); }
      finally { setBusy(button, false, ""); }
    });
  }
  // A launcher may reuse an existing tab; a fragment-only navigation does not reload it.
  window.addEventListener("hashchange", () => {
    if (window.location.hash.startsWith("#ticket=")) void enterFromSsh();
  });
  void enterFromSsh();
})();
""".encode("utf-8")


ASSETS = {
    "html": ("text/html; charset=utf-8", ADMIN_HTML),
    "css": ("text/css; charset=utf-8", ADMIN_CSS),
    "js": ("text/javascript; charset=utf-8", ADMIN_JS),
}
