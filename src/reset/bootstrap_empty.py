#!/usr/bin/python3
"""Offline first-start preparation for a provably empty legacy service.

This never clears a world or increments its generation. Unknown legacy data is
refused and left in place. The service configuration must already point its
XDG data directory at the dedicated world root.
"""
import os
import datetime
import re
from pathlib import Path
import pwd
import secrets
import sqlite3
import sys
import time
import migrate
import worker

# Anything outside this narrowly understood inventory requires manual review.
_ALLOWED = {'saves': ('directory',), 'saves/selection.cfg': None, 'logs': ('directory',),
            'mods': ('directory',), 'feedback': ('directory',)}
_EMPTY_TABLES = ('assignments', 'plot_owners', 'plot_houses', 'plot_house_history',
                 'plot_capture_events', 'official_world_maps', 'plot_snapshot_revisions',
                 'world_entry_readiness', 'world_start_authorizations', 'world_reset_jobs',
                 'world_bootstrap_jobs', 'node_monitor_state', 'node_connections')

def _empty_source(source):
    entries = migrate.manifest(source)
    for path, detail in entries.items():
        if path not in _ALLOWED:
            match = re.fullmatch(r'logs/godot(\d{4}-\d{2}-\d{2}T\d{2}\.\d{2}\.\d{2})\.log', path)
            if path != 'logs/godot.log' and not match:
                raise RuntimeError('来源包含未知文件或可能的世界数据')
            if match:
                try: datetime.datetime.strptime(match.group(1), '%Y-%m-%dT%H.%M.%S')
                except ValueError: raise RuntimeError('日志文件日期无效')
            if detail[0] != 'file': raise RuntimeError('日志类型不正确')
        if path in ('saves', 'logs', 'mods', 'feedback') and detail != ('directory',): raise RuntimeError('目录类型不正确')
        if path == 'saves/selection.cfg' and detail[0] != 'file': raise RuntimeError('选择配置类型不正确')
    return entries


def _empty_control(db, world):
    row = db.execute('SELECT status,generation,boot_nonce FROM worlds WHERE id=?', (world,)).fetchone()
    if row != ('open', 1, ''): raise RuntimeError('世界身份或世代不是可验证的初始状态')
    for table in _EMPTY_TABLES:
        if not db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (table,)).fetchone():
            raise RuntimeError('控制数据库缺少初始核对表')
        if db.execute(f'SELECT COUNT(*) FROM {table} WHERE world_id=?', (world,)).fetchone()[0]:
            raise RuntimeError('控制库已有世界、地块或历史记录')
    # Online rows have no world_id. Without a trustworthy association, even a
    # row from an unknown node is evidence that the database is not pristine.
    if not db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='node_online'").fetchone():
        raise RuntimeError('控制数据库缺少在线玩家核对表')
    if db.execute('SELECT COUNT(*) FROM node_online').fetchone()[0]:
        raise RuntimeError('控制库已有在线玩家记录')
    return row[1]


def bootstrap(cfg, source, migration_backups, system, uid, gid, verify_idle, wait_seconds=90):
    # Refuse startup if the shared checkpoint protocol is unavailable.
    if not worker._world_protocol_ready(): raise RuntimeError('首次世界启动协议尚未启用')
    if system.active(): raise RuntimeError('必须先停止旧世界服务')
    verify_idle()
    _empty_source(source)
    job = secrets.token_hex(16)
    nonce = secrets.token_hex(16)
    owner = (uid, gid)
    started = False
    committed = False
    backup = None
    db = worker._connect(cfg)
    try:
        # Block control-plane mutations while checking and backing up both sides.
        db.execute('BEGIN IMMEDIATE')
        generation = _empty_control(db, cfg.world_id)
        backup = migrate.migrate(source, cfg.world_root, migration_backups, system, uid, gid, verify_idle)
        # A separate read connection can back up the stable committed view while
        # this process holds the only SQLite writer lock.
        with sqlite3.connect(cfg.database, timeout=5) as reader:
            worker._backup_db(reader, backup / 'control.sqlite')
        if _empty_source(source) != _empty_source(backup / 'world'):
            raise RuntimeError('备份与来源不一致')
        if _empty_control(db, cfg.world_id) != generation or system.active():
            raise RuntimeError('首次启动前的世界状态发生变化')
        verify_idle()
        now = int(time.time())
        db.execute('INSERT INTO world_bootstrap_jobs(id,world_id,phase,created,updated) VALUES(?,?,?,?,?)',
                   (job, cfg.world_id, 'starting', now, now))
        changed = db.execute("UPDATE worlds SET status='closed',boot_nonce=? WHERE id=? AND status='open' AND generation=? AND boot_nonce=''",
                             (nonce, cfg.world_id, generation))
        if changed.rowcount != 1: raise RuntimeError('世界状态发生变化')
        db.execute('INSERT INTO world_start_authorizations VALUES(?,?,?,?,?,?)',
                   (cfg.world_id, job, cfg.node_id, generation, nonce, 'bootstrap'))
        proof_path, proof = worker._write_start_proof(cfg, job, generation, nonce, 'fresh', owner)
        receipt = cfg.receipt_dir / 'start-receipt.json'
        try: receipt.unlink()
        except FileNotFoundError: pass
        worker._atomic_json(cfg.receipt_dir, 'start-request.json',
                            {'nonce': nonce, 'world_id': cfg.world_id, 'generation': generation,
                             'world_root': str(cfg.world_root)}, owner)
        db.execute('COMMIT'); committed = True
        started = True
        system.start()
        pid = system.pid()
        deadline = time.monotonic() + wait_seconds
        while time.monotonic() < deadline:
            if worker._start_ready(cfg, system, db, job, generation, nonce, owner, proof_path, proof,
                                   receipt, pid, 'fresh', checkpoint_protocol=True, bootstrap_protocol=True): break
            time.sleep(.25)
        else: raise RuntimeError('首次世界启动未通过检查')
        db.execute('BEGIN IMMEDIATE')
        if not worker._start_ready(cfg, system, db, job, generation, nonce, owner, proof_path, proof,
                                   receipt, pid, 'fresh', checkpoint_protocol=True, bootstrap_protocol=True):
            raise RuntimeError('开放前启动证明或地图发生变化')
        changed = db.execute("UPDATE world_bootstrap_jobs SET phase='complete',updated=? WHERE id=? AND phase='starting'",
                             (int(time.time()), job))
        opened = db.execute("UPDATE worlds SET status='open' WHERE id=? AND status='closed' AND generation=? AND boot_nonce=?",
                            (cfg.world_id, generation, nonce))
        if changed.rowcount != 1 or opened.rowcount != 1: raise RuntimeError('世界状态发生变化')
        db.execute('INSERT INTO audit(action,target,at) VALUES(?,?,?)',
                   ('world_bootstrap_complete', cfg.world_id + '/' + job, int(time.time())))
        db.execute('COMMIT')
        return backup
    except Exception:
        if db.in_transaction: db.execute('ROLLBACK')
        if started:
            try: system.stop()
            except Exception: pass
        if committed:
            db.execute("UPDATE world_bootstrap_jobs SET phase='failed',updated=? WHERE id=? AND phase='starting'", (int(time.time()), job))
        raise
    finally:
        db.close()


def main():
    if os.geteuid() != 0 or len(sys.argv) != 1: raise SystemExit('只能由 root 使用固定路径运行，不接受参数')
    cfg = worker.load_config()
    if cfg.world_root.exists() or cfg.world_root.is_symlink(): raise SystemExit('目标已存在，拒绝覆盖')
    system = worker.Systemd()
    account = pwd.getpwnam('wheatstead-world')
    migrate.assert_no_world_user_processes(account.pw_uid)
    worker._safe_absolute(migrate.SOURCE, True)
    worker._safe_absolute(migrate.BACKUPS, True)
    info = migrate.BACKUPS.stat()
    if info.st_uid != 0 or info.st_mode & 0o077: raise SystemExit('迁移备份目录必须仅 root 可访问')
    worker._root_managed_parents(migrate.BACKUPS / 'child')
    backup = bootstrap(cfg, migrate.SOURCE, migrate.BACKUPS, system, account.pw_uid, account.pw_gid,
                       lambda: migrate.assert_no_world_user_processes(account.pw_uid))
    print('首次世界启动已核对，旧目录与控制库备份：', backup)

if __name__ == '__main__': main()
