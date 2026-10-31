let csrf = "";
const notice = document.getElementById("notice");
const worldList = document.getElementById("world-list");
const nodeRows = document.getElementById("node-rows");
const auditRows = document.getElementById("audit-rows");
const metrics = document.getElementById("metrics");
const metricsTime = document.getElementById("metrics-time");
const playerSnapshots = document.getElementById("player-snapshots");
const connectionRows = document.getElementById("connection-rows");
let loading = false;

function el(tag, text, cls) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (cls) node.className = cls;
  return node;
}
function timeText(seconds) {
  if (!seconds) return "—";
  return new Date(seconds * 1000).toLocaleString("zh-CN", { hour12: false });
}
function bytes(value) {
  if (typeof value !== "number" || value < 0) return "不可用";
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return value.toFixed(unit ? 1 : 0) + " " + units[unit];
}
function metric(label, value) {
  const card = el("div", undefined, "card");
  card.appendChild(el("span", label));
  card.appendChild(el("strong", value));
  metrics.appendChild(card);
}
function showMetrics(m) {
  metrics.replaceChildren();
  metric("CPU 使用率", typeof m.cpu_percent === "number" ? m.cpu_percent.toFixed(1) + "%" : "等待采样或不可用");
  metric("内存可用 / 总量", bytes(m.memory_available_bytes) + " / " + bytes(m.memory_total_bytes));
  metric("数据库磁盘可用 / 总量", bytes(m.disk_available_bytes) + " / " + bytes(m.disk_total_bytes));
  metric("世界服务 CPU", typeof m.world_cpu_percent === "number" ? m.world_cpu_percent.toFixed(1) + "%" : "等待采样或不可用");
  metric("世界服务内存", bytes(m.world_memory_bytes));
  metric("后台进程内存", bytes(m.api_memory_bytes));
  metric("后台运行时间", typeof m.api_uptime_seconds === "number" ? Math.floor(m.api_uptime_seconds / 3600) + " 小时 " + Math.floor(m.api_uptime_seconds % 3600 / 60) + " 分" : "不可用");
  metricsTime.textContent = "采样时间：" + timeText(m.sampled_at) + "。世界服务内存为其进程组总用量；世界服务 CPU 的 100% 相当于占用一个 CPU 核心。";
}
async function manage(action, world, steam) {
  const response = await fetch("/admin/multiplayer/manage", {
    method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-Token": csrf },
    body: JSON.stringify({ action, world, steam: steam || "" })
  });
  if (!response.ok) { notice.textContent = "操作失败：" + response.status; return; }
  await load();
}
async function resetWorld(world) {
  if (!window.confirm("重置会断开所有玩家，清空这个官方世界的建筑、地块、物品和玩家角色及背包。重置期间世界会关闭。确定继续吗？")) return;
  const confirmation = window.prompt("请输入世界 ID「" + world + "」确认重置：");
  if (confirmation === null) return;
  if (confirmation !== world) { notice.textContent = "世界 ID 不匹配，没有提交重置。"; return; }
  try {
    const response = await fetch("/admin/multiplayer/reset", {
      method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-Token": csrf },
      body: JSON.stringify({ world, confirmation })
    });
    if (!response.ok) {
      const result = await response.json().catch(() => ({}));
      const reason = { reset_unavailable: "重置执行器未启用或世界不受支持", reset_worker_offline: "重置执行器未就绪", reset_in_progress: "已有重置任务", world_unavailable: "世界无法重置", invalid_confirmation: "世界 ID 不匹配" }[result.code];
      notice.textContent = "重置未提交：" + (reason || "请求失败（" + response.status + "）");
      return;
    }
    await load();
  } catch (_) { notice.textContent = "重置请求发送失败，请先刷新检查任务状态。"; }
}
async function load() {
  if (loading) return;
  loading = true;
  try {
  const response = await fetch("/admin/multiplayer/data", { cache: "no-store" });
  if (response.status === 401) { location.href = "/admin"; return; }
  if (!response.ok) { notice.textContent = "读取失败：" + response.status; return; }
  const data = await response.json();
  csrf = data.csrf || "";
  showMetrics(data.metrics || {});
  const serverNow = data.metrics?.sampled_at || Date.now() / 1000;
  notice.textContent = "共 " + data.worlds.length + " 个世界；节点心跳超过 90 秒显示离线。";
  worldList.replaceChildren();
  for (const item of data.worlds) {
    const world = item.world;
    // A missing field from an older server must not enable unsafe operations.
    const registered = item.map_registered !== false;
    const card = el("article", undefined, "card");
    card.appendChild(el("h3", world.name));
    card.appendChild(el("p", "ID：" + world.id + " · 地图：" + world.map_id + " · 排名：" + (world.ranked ? "入榜" : "不入榜")));
    card.appendChild(el("p", "状态：" + (world.status === "open" ? "开放" : "关闭") + " · 已分配地块：" + world.players + "/" + world.capacity));
    card.appendChild(el("p", registered
      ? "玩家入场：" + (item.entry_ready === true ? "已就绪" : "不可用")
      : "玩家入场：尚未登记官方地图"));
    const actions = el("div", undefined, "actions");
    const resetState = item.reset || {};
    const resetPending = resetState.phase && !["complete", "recovered"].includes(resetState.phase);
    const toggle = el("button", world.status === "open" ? "关闭世界" : "开放世界");
    toggle.disabled = registered || !!resetPending;
    toggle.addEventListener("click", () => manage(world.status === "open" ? "close" : "open", world.id));
    actions.appendChild(toggle);
    const reset = el("button", "重置世界", "danger");
    reset.disabled = !resetState.available;
    reset.addEventListener("click", () => resetWorld(world.id));
    actions.appendChild(reset);
    card.appendChild(actions);
    if (registered) card.appendChild(el("p", "为保护世界数据，已登记的官方世界暂不支持手动开关和释放地块。"));
    const stateNames = { queued: "等待执行", stopping: "正在停止世界", backing_up: "正在备份并切换存档", starting: "正在启动新世界", complete: "重置完成", failed: "重置失败", recovering: "正在恢复", recovered: "已恢复" };
    let resetInfo = resetState.phase ? "重置任务：" + (stateNames[resetState.phase] || resetState.phase) : "还没有重置任务。";
    if (resetState.updated) resetInfo += " · 更新时间：" + timeText(resetState.updated);
    if (resetState.phase === "complete" && resetState.updated) resetInfo += " · 下次手动重置参考时间：" + timeText(resetState.updated + 7 * 86400);
    if (resetState.error) resetInfo += " · 原因：" + resetState.error;
    if (!resetState.enabled) resetInfo += " · 尚未启用这个世界的重置执行器。";
    else if (resetState.phase === "failed") resetInfo += " · 需在服务器处理故障。";
    else if (resetPending && (!resetState.worker_last_seen || serverNow - resetState.worker_last_seen < 0 || serverNow - resetState.worker_last_seen > 90))
      resetInfo += " · 执行器未响应，请检查服务器；世界保持关闭。";
    else if (resetPending) resetInfo += " · 请等待当前任务完成，世界保持关闭。";
    else if (world.status !== "open") resetInfo += " · 世界未开放，无法提交重置。";
    else if (!resetState.available && (!resetState.worker_last_seen || serverNow - resetState.worker_last_seen < 0 || serverNow - resetState.worker_last_seen > 90))
      resetInfo += " · 未收到重置执行器心跳。";
    else if (!resetState.available && item.entry_ready !== true) resetInfo += " · 世界尚未就绪，无法提交重置。";
    else if (!resetState.available) resetInfo += " · 当前无法提交重置，请检查世界和执行器状态。";
    card.appendChild(el("p", resetInfo));
    const table = el("table");
    const head = el("thead");
    const headRow = el("tr");
    for (const text of ["初始玩家 SteamID", "当前主人 SteamID", "地块", "出生点", "房屋种子", "选择时间", "操作"]) headRow.appendChild(el("th", text));
    head.appendChild(headRow); table.appendChild(head);
    const body = el("tbody");
    if (!item.assignments.length) {
      const row = el("tr"); const cell = el("td", "还没有玩家选地"); cell.colSpan = 7; row.appendChild(cell); body.appendChild(row);
    }
    for (const assignment of item.assignments) {
      const row = el("tr");
      for (const value of [assignment.steam_id, item.plot_owners?.[assignment.plot_id] || "未知", assignment.plot_id, assignment.spawn_id, assignment.house_seed, timeText(assignment.created)])
        row.appendChild(el("td", value));
      const cell = el("td");
      const release = el("button", "释放地块");
      release.className = "danger";
      release.disabled = registered || !!resetPending;
      release.addEventListener("click", () => manage("release", world.id, assignment.steam_id));
      cell.appendChild(release); row.appendChild(cell); body.appendChild(row);
    }
    table.appendChild(body);
    const wrap = el("div", undefined, "table-wrap"); wrap.appendChild(table); card.appendChild(wrap);
    worldList.appendChild(card);
  }
  nodeRows.replaceChildren();
  if (!data.nodes.length) {
    const row = el("tr"); const cell = el("td", "还没有节点心跳"); cell.colSpan = 6; row.appendChild(cell); nodeRows.appendChild(row);
  }
  for (const node of data.nodes) {
    const row = el("tr");
    row.appendChild(el("td", node.name + "（" + node.id + "）"));
    row.appendChild(el("td", node.region || "—"));
    row.appendChild(el("td", node.version || "—"));
    row.appendChild(el("td", String(node.players)));
    row.appendChild(el("td", timeText(node.last_seen)));
    row.appendChild(el("td", serverNow - node.last_seen >= 0 && serverNow - node.last_seen < 90 ? "在线" : "离线"));
    nodeRows.appendChild(row);
  }
  const playerMonitor = data.player_monitor || { snapshots: [], online: [], events: [] };
  playerSnapshots.replaceChildren();
  if (!playerMonitor.snapshots.length) playerSnapshots.appendChild(el("p", "尚未收到支持玩家快照的节点心跳。"));
  for (const snapshot of playerMonitor.snapshots) {
    const fresh = serverNow - snapshot.last_seen >= 0 && serverNow - snapshot.last_seen < 90;
    const players = playerMonitor.online.filter(p => p.node_id === snapshot.node_id).map(p => p.steam_id);
    const card = el("article", undefined, "card");
    card.appendChild(el("h3", "世界 " + snapshot.world_id + " · 节点 " + snapshot.node_id));
    card.appendChild(el("p", fresh ? "已认证在线：" + players.length + " 人" : "在线状态未知，最后快照：" + timeText(snapshot.last_seen)));
    if (fresh && players.length) card.appendChild(el("p", players.join("、")));
    playerSnapshots.appendChild(card);
  }
  connectionRows.replaceChildren();
  if (!playerMonitor.events.length) {
    const row = el("tr"); const cell = el("td", "尚未收到连接记录"); cell.colSpan = 6; row.appendChild(cell); connectionRows.appendChild(row);
  }
  for (const event of playerMonitor.events) {
    const row = el("tr");
    for (const value of [event.world_id, event.node_id, event.steam_id, event.type === "connect" ? "连接" : "断开", timeText(event.at), timeText(event.received)])
      row.appendChild(el("td", value));
    connectionRows.appendChild(row);
  }
  auditRows.replaceChildren();
  for (const entry of data.audit) {
    const row = el("tr");
    row.appendChild(el("td", entry.action));
    row.appendChild(el("td", entry.target));
    row.appendChild(el("td", timeText(entry.at)));
    auditRows.appendChild(row);
  }
  } catch (_) { notice.textContent = "读取失败，请检查连接后刷新。"; }
  finally { loading = false; }
}
document.getElementById("refresh").addEventListener("click", load);
document.getElementById("leaderboard").addEventListener("click", () => { location.href = "/admin"; });
document.getElementById("logout").addEventListener("click", async () => {
  await fetch("/admin/logout", { method: "POST", headers: { "X-CSRF-Token": csrf } });
  location.href = "/admin";
});
load();
setInterval(load, 15000);
