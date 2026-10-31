#!/usr/bin/python3
"""受限的本机世界重置 worker；不会从后台任务读取路径或命令。"""
import dataclasses
import fcntl
import hashlib
import json
import os
import re
import secrets
import shutil
import sqlite3
import stat
import subprocess
import sys
import time
from pathlib import Path

CONFIG = Path('/etc/wheatstead-reset/worker.json')
SYSTEMCTL = '/usr/bin/systemctl'
UNIT = 'wheatstead-world.service'
WORLD_ROOT = '/var/lib/wheatstead-world-data/godot/app_userdata/Wheatstead'
DATABASE = '/var/lib/wheatstead-playtest/playtest-monitor.db.leaderboard.multiplayer'


@dataclasses.dataclass(frozen=True)
class Config:
    world_id: str
    node_id: str
    world_root: Path
    backup_dir: Path
    database: Path
    receipt_dir: Path
    map_protocol: int = 0


def _fsync_dir(path):
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try: os.fsync(fd)
    finally: os.close(fd)


def _atomic_json(directory, name, value, owner=None):
    data = json.dumps(value, separators=(',', ':')).encode()
    temp = directory / ('.' + name + '.' + secrets.token_hex(8))
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        if owner is not None: os.fchown(fd, *owner)
        with os.fdopen(fd, 'wb') as out:
            out.write(data)
            out.flush()
            os.fsync(out.fileno())
        os.replace(temp, directory / name)
        _fsync_dir(directory)
    finally:
        try: temp.unlink()
        except FileNotFoundError: pass


def _proof_directory(cfg, service_owner):
    if os.geteuid() == 0 and service_owner[0] == 0:
        raise RuntimeError('世界服务不得以 root 运行')
    parent = cfg.receipt_dir.parent
    parent_info = parent.lstat()
    if not stat.S_ISDIR(parent_info.st_mode) or parent_info.st_uid != os.geteuid() or parent_info.st_mode & 0o022:
        raise RuntimeError('启动证明上级目录不安全')
    directory = parent / 'wheatstead-start-proofs'
    try:
        directory.mkdir(mode=0o750)
        os.chown(directory, os.geteuid(), service_owner[1])
        os.chmod(directory, 0o750)
        _fsync_dir(parent)
    except FileExistsError:
        pass
    info = directory.lstat()
    if (not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid() or info.st_gid != service_owner[1]
        or stat.S_IMODE(info.st_mode) != 0o750):
        raise RuntimeError('启动证明目录不安全')
    return directory


def _write_start_proof(cfg, job, generation, nonce, mode, service_owner):
    if mode not in ('fresh', 'restore') or not re.fullmatch(r'[0-9a-f]{32}', job):
        raise RuntimeError('启动证明参数无效')
    directory = _proof_directory(cfg, service_owner)
    proof = {'version': 1, 'job_id': job, 'mode': mode, 'world_id': cfg.world_id,
             'node_id': cfg.node_id, 'generation': generation, 'nonce': nonce,
             'world_root': str(cfg.world_root)}
    path = directory / (cfg.world_id + '.json')
    temp = directory / ('.proof-' + secrets.token_hex(16))
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o440)
    try:
        os.fchown(fd, os.geteuid(), service_owner[1]); os.fchmod(fd, 0o440)
        with os.fdopen(fd, 'wb') as out:
            out.write(json.dumps(proof, separators=(',', ':')).encode())
            out.flush(); os.fsync(out.fileno())
        os.replace(temp, path); _fsync_dir(directory)
    finally:
        try: temp.unlink()
        except FileNotFoundError: pass
    return path, proof


def _verify_start_proof(path, expected, service_owner):
    directory = path.parent
    parent = directory.parent.lstat()
    if not stat.S_ISDIR(parent.st_mode) or parent.st_uid != os.geteuid() or parent.st_mode & 0o022: return False
    info = directory.lstat()
    if (not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid() or info.st_gid != service_owner[1]
        or stat.S_IMODE(info.st_mode) != 0o750): return False
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    try:
        info = os.fstat(fd)
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_gid != service_owner[1]
            or stat.S_IMODE(info.st_mode) != 0o440 or info.st_size > 4096): return False
        with os.fdopen(fd, 'rb', closefd=False) as stream: actual = json.load(stream)
        return _same_json(actual, expected)
    finally: os.close(fd)


def _safe_absolute(path, directory):
    path = Path(path)
    if not path.is_absolute() or path == Path('/') or str(path) != os.path.realpath(path):
        raise ValueError('路径必须是已存在且不包含符号链接的绝对路径')
    if directory and not path.is_dir(): raise ValueError('目录不存在')
    if not directory and not path.is_file(): raise ValueError('数据库不存在')
    return path


def _safe_world_root(path):
    path = Path(path)
    if not path.is_absolute() or path == Path('/') or str(path) != os.path.realpath(path):
        raise ValueError('世界目录必须是无符号链接的绝对路径')
    if path.exists() and not path.is_dir(): raise ValueError('世界目录不是目录')
    if not path.parent.is_dir(): raise ValueError('世界目录的父目录不存在')
    return path


def _root_managed_parents(path):
    path = path.parent
    while True:
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            raise ValueError('目录上级必须由 root 所有且不可被其他用户写入')
        if path == path.parent: break
        path = path.parent


def _root_marker(root, world):
    marker = root / '.wheatstead-world-root'
    info = marker.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
        raise ValueError('世界根目录标记不安全')
    if marker.read_text(encoding='utf-8').strip() != world: raise ValueError('世界根目录标记不匹配')


def _sync_tree(root):
    for parent, dirs, files in os.walk(root, topdown=False, followlinks=False):
        for name in dirs + files:
            path = Path(parent) / name
            mode = path.lstat().st_mode
            if stat.S_ISLNK(mode) or not (stat.S_ISDIR(mode) or stat.S_ISREG(mode)):
                raise RuntimeError('世界数据包含不支持的文件类型')
            if stat.S_ISREG(mode):
                fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
                try: os.fsync(fd)
                finally: os.close(fd)
        _fsync_dir(parent)


# Legacy calls without the checkpoint protocol must refuse any checkpoint trace.
def _has_checkpoint_artifacts(root, ignore_reserved=False):
    if not ignore_reserved and ((root / 'official-checkpoints').exists() or (root / 'official-checkpoints').is_symlink()):
        return True
    def fail(error): raise error
    for parent, dirs, files in os.walk(root, followlinks=False, onerror=fail):
        if ignore_reserved and Path(parent) == root:
            dirs[:] = [name for name in dirs if name != 'official-checkpoints']
            files = [name for name in files if name != 'official-checkpoints']
        for name in dirs + files:
            if name == 'checkpoint.lock' or re.fullmatch(r'snapshot-[0-9a-f]{32}\.json', name):
                return True
    return False


def _copy_non_world(source, destination):
    if _has_checkpoint_artifacts(source, ignore_reserved=True):
        raise RuntimeError('发现未知位置的世界检查点，不能复制新世界')
    # 专服 user:// 还包含模组、设置、日志和重放账本；只排除已确认的世界存档。
    def ignored(directory, names):
        if Path(directory) != source: return set()
        excluded = {'saves', 'story_years', 'official-checkpoints'}
        for name in names:
            if name.startswith(('save.now.json', 'save.old.json')) or re.fullmatch(r'save_[123](?:\..*)?\.json(?:\..*)?', name):
                excluded.add(name)
        return excluded
    shutil.copytree(source, destination, copy_function=shutil.copy2, ignore=ignored)
    for parent, dirs, files in os.walk(destination, topdown=False):
        for name in dirs + files:
            target = Path(parent) / name
            info = (source / target.relative_to(destination)).stat()
            os.chown(target, info.st_uid, info.st_gid)
    info = source.stat()
    os.chown(destination, info.st_uid, info.st_gid)
    _root_marker(destination, 'official-1')
    _sync_tree(destination)


def load_config():
    # 唯一的配置来源是 root 所有且不能被其他用户写入的文件和父目录。
    for path in (CONFIG.parent, CONFIG):
        st = path.lstat()
        if stat.S_ISLNK(st.st_mode) or st.st_uid != 0 or st.st_mode & 0o022:
            raise ValueError('worker 配置及目录须由 root 所有且不可被其他用户写入')
    data = json.loads(CONFIG.read_text(encoding='utf-8'))
    required = {'world_id', 'node_id', 'world_root', 'backup_dir', 'database', 'receipt_dir'}
    if not required <= set(data) or set(data) - required - {'world_map_protocol'}:
        raise ValueError('worker 配置字段不正确')
    if 'world_map_protocol' in data and (type(data['world_map_protocol']) is not int or data['world_map_protocol'] != 1):
        raise ValueError('世界地图协议版本不正确')
    if data['world_id'] != 'official-1' or data['world_root'] != WORLD_ROOT or data['database'] != DATABASE:
        raise ValueError('配置必须指向已盘点的官方世界和多人数据库')
    if any(not isinstance(data[k], str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,64}', data[k]) for k in ('world_id', 'node_id')):
        raise ValueError('世界或节点 ID 不正确')
    cfg = Config(data['world_id'], data['node_id'], _safe_world_root(data['world_root']),
                 _safe_absolute(data['backup_dir'], True), _safe_absolute(data['database'], False),
                 _safe_absolute(data['receipt_dir'], True), data.get('world_map_protocol', 0))
    roots = (cfg.world_root, cfg.backup_dir, cfg.receipt_dir)
    if any(a == b or a in b.parents or b in a.parents for i, a in enumerate(roots) for b in roots[i + 1:]):
        raise ValueError('世界、备份和停机凭证目录必须互不包含')
    if any(root == cfg.database or root in cfg.database.parents for root in roots):
        raise ValueError('数据库必须位于这些目录之外')
    if cfg.world_root.parent.stat().st_dev != cfg.backup_dir.stat().st_dev:
        raise ValueError('备份目录必须与世界目录在同一文件系统')
    if cfg.backup_dir.stat().st_uid != 0 or cfg.backup_dir.stat().st_mode & 0o077:
        raise ValueError('备份目录必须由 root 所有且只允许 root 访问')
    for path in (cfg.world_root, cfg.backup_dir, cfg.receipt_dir): _root_managed_parents(path)
    if cfg.world_root.exists(): _root_marker(cfg.world_root, cfg.world_id)
    return cfg


class Systemd:
    def _call(self, *args):
        return subprocess.run([SYSTEMCTL, *args, UNIT], stdin=subprocess.DEVNULL,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True,
                              timeout=90, text=True).stdout.strip()

    def active(self):
        result = subprocess.run([SYSTEMCTL, 'is-active', UNIT], stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=90, text=True)
        state = result.stdout.strip()
        if result.returncode == 0 and state == 'active': return True
        if result.returncode == 3 and state in ('inactive', 'failed'): return False
        raise RuntimeError('无法确认世界服务状态')

    def pid(self):
        value = self._call('show', '--property=MainPID', '--value')
        if not value.isdecimal() or int(value) <= 1: raise RuntimeError('世界进程 PID 不可用')
        return int(value)

    def credentials(self, pid):
        process = os.stat('/proc/' + str(pid))
        values = {}
        with open('/proc/' + str(pid) + '/status', encoding='ascii') as status:
            for line in status:
                key, sep, value = line.partition(':')
                if sep and key in ('CapEff', 'CapPrm', 'CapAmb', 'NoNewPrivs'):
                    values[key] = value.strip()
        if (any(key not in values for key in ('CapEff', 'CapPrm', 'CapAmb', 'NoNewPrivs'))
            or any(int(values[key], 16) != 0 for key in ('CapEff', 'CapPrm', 'CapAmb'))
            or values['NoNewPrivs'] != '1'):
            raise RuntimeError('世界进程必须禁用特权和 Linux capabilities')
        return process.st_uid, process.st_gid

    def stop(self):
        self._call('stop')
        if self.active(): raise RuntimeError('世界服务仍在运行')
        result = self._call('show', '--property=Result', '--value')
        status = self._call('show', '--property=ExecMainStatus', '--value')
        if result != 'success' or status != '0': raise RuntimeError('世界进程未正常退出')

    def start(self):
        self._call('start')
        if not self.active(): raise RuntimeError('世界服务未启动')


def _connect(cfg):
    db = sqlite3.connect(cfg.database, timeout=5, isolation_level=None)
    db.execute('PRAGMA foreign_keys=ON')
    return db


def _phase(db, job, expected, target, error=''):
    result = db.execute('UPDATE world_reset_jobs SET phase=?,error=?,updated=? WHERE id=? AND phase=?',
                        (target, error, int(time.time()), job, expected))
    if result.rowcount != 1: raise RuntimeError('重置任务状态已变化')


def _save_stamps(root):
    result = {}
    for directory, dirs, files in os.walk(root, followlinks=False):
        dirs[:] = [d for d in dirs if not (Path(directory) / d).is_symlink()]
        if 'save.now.json' in files:
            file = Path(directory) / 'save.now.json'
            if file.is_symlink() or not file.is_file(): continue
            s = file.stat()
            result[str(file.relative_to(root))] = (s.st_mtime_ns, s.st_size, s.st_ino)
    return result


def _validated_save(root, before):
    for relative, stamp in _save_stamps(root).items():
        if before.get(relative) == stamp: continue
        file = root / relative
        with file.open('rb') as handle:
            if file.stat().st_size > 64 * 1024 * 1024: continue
            data = json.load(handle)
            if (isinstance(data, dict) and isinstance(data.get('world'), dict)
                and isinstance(data.get('multiplayer'), dict) and isinstance(data.get('player'), dict)
                and isinstance(data['player'].get('scene'), str) and data['player']['scene'].strip()): return True
    return False


def _same_json(left, right):
    if isinstance(left, bool) or isinstance(right, bool): return type(left) is type(right) and left == right
    if isinstance(left, (int, float)) and isinstance(right, (int, float)): return left == right
    if type(left) is not type(right): return False
    if isinstance(left, dict): return left.keys() == right.keys() and all(_same_json(left[k], right[k]) for k in left)
    if isinstance(left, list): return len(left) == len(right) and all(_same_json(a, b) for a, b in zip(left, right))
    return left == right


def _same_world_map(left, right):
    if not isinstance(left, dict) or not isinstance(right, dict): return False
    def canonical(value):
        value = dict(value)
        for key in ('plots', 'spawns'):
            items = value.get(key)
            if not isinstance(items, list) or any(not isinstance(item, dict) or not isinstance(item.get('id'), str) for item in items):
                return None
            value[key] = sorted(items, key=lambda item: item['id'])
        return value
    a, b = canonical(left), canonical(right)
    return a is not None and b is not None and _same_json(a, b)


def _validated_registered_map(cfg, db, generation, relative, service_owner):
    if not isinstance(relative, str): return False
    parts = relative.split('/')
    if not (parts == ['save.now.json'] or len(parts) == 3 and parts[0] == 'saves'
            and re.fullmatch(r'[A-Za-z0-9_-]{1,128}', parts[1]) and parts[2] == 'save.now.json'):
        return False
    row = db.execute('SELECT generation,map_json FROM official_world_maps WHERE world_id=?', (cfg.world_id,)).fetchone()
    if not row or row[0] != generation: return False
    registered = json.loads(row[1])
    if not isinstance(registered, dict) or registered.get('world_id') != cfg.world_id or registered.get('generation') != generation:
        return False
    dirs = []
    fd = None
    try:
        dirs.append(os.open(cfg.world_root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW))
        for name in parts[:-1]:
            dirs.append(os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=dirs[-1]))
        fd = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW, dir_fd=dirs[-1])
        before = os.fstat(fd)
        if (not stat.S_ISREG(before.st_mode) or before.st_uid != service_owner[0]
            or before.st_size <= 0 or before.st_size > 64 * 1024 * 1024): return False
        with os.fdopen(fd, 'rb', closefd=False) as stream: saved = json.load(stream)
        after = os.fstat(fd)
        current = os.stat(parts[-1], dir_fd=dirs[-1], follow_symlinks=False)
        stamp = lambda s: (s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns)
        if stamp(before) != stamp(after) or stamp(after) != stamp(current): return False
        if (not isinstance(saved, dict) or not isinstance(saved.get('world'), dict)
            or not isinstance(saved.get('multiplayer'), dict) or not isinstance(saved.get('player'), dict)
            or not isinstance(saved['player'].get('scene'), str) or not saved['player']['scene'].strip()
            or not isinstance(saved.get('world_generation'), dict)
            or not _same_world_map(saved['world_generation'].get('official_map'), registered)): return False
        world_data = saved['world_generation']
        for key in ('seed', 'terrain_version', 'biome_scale', 'town_x', 'town_y', 'home_x', 'home_y'):
            if key not in world_data or key not in registered or not _same_json(world_data[key], registered[key]): return False
        os.fsync(fd)
        for directory in reversed(dirs): os.fsync(directory)
        return stamp(os.fstat(fd)) == stamp(before) == stamp(os.stat(parts[-1], dir_fd=dirs[-1], follow_symlinks=False))
    finally:
        if fd is not None: os.close(fd)
        for directory in reversed(dirs): os.close(directory)


def _validated_checkpoint(cfg, generation, service_owner, save_path, require_lock=True):
    """Validate a stopped world's committed checkpoint, without changing it.

    Caller must have stopped the only world process. An outstanding house event
    is deliberately refused: the reset must not erase an unreconciled mutation.
    """
    if _has_checkpoint_artifacts(cfg.world_root, ignore_reserved=True): return False
    if not isinstance(save_path, str): return False
    parts = save_path.split('/')
    if not (parts == ['save.now.json'] or len(parts) == 3 and parts[0] == 'saves'
            and re.fullmatch(r'[A-Za-z0-9_-]{1,128}', parts[1]) and parts[2] == 'save.now.json'): return False
    opened = []
    try:
        def open_dir(parent, name):
            fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
            opened.append(fd)
            return fd
        def read_file(parent, name, limit):
            fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=parent)
            opened.append(fd)
            info = os.fstat(fd)
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != service_owner[0]
                or info.st_nlink != 1 or info.st_mode & 0o022 or not 0 < info.st_size <= limit):
                raise ValueError('检查点文件权限或长度不正确')
            with os.fdopen(fd, 'rb', closefd=False) as stream: data = stream.read(limit + 1)
            after = os.fstat(fd); current = os.stat(name, dir_fd=parent, follow_symlinks=False)
            stamp = lambda s: (s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns)
            if len(data) != info.st_size or stamp(info) != stamp(after) or stamp(after) != stamp(current):
                raise ValueError('检查点文件发生变化')
            return data
        worldfd = os.open(cfg.world_root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        opened.append(worldfd)
        checkpoint = open_dir(worldfd, 'official-checkpoints')
        directory = os.fstat(checkpoint)
        if directory.st_uid != service_owner[0] or stat.S_IMODE(directory.st_mode) != 0o700: return False
        lockfd = os.open('checkpoint.lock', os.O_WRONLY | os.O_NOFOLLOW, dir_fd=checkpoint)
        opened.append(lockfd)
        lockinfo = os.fstat(lockfd)
        if not stat.S_ISREG(lockinfo.st_mode) or lockinfo.st_uid != service_owner[0] or lockinfo.st_nlink != 1: return False
        if require_lock: fcntl.flock(lockfd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        marker_bytes = read_file(checkpoint, 'current.json', 4096)
        marker = json.loads(marker_bytes)
        if (not isinstance(marker, dict) or set(marker) != {'version', 'world_id', 'generation', 'file', 'length', 'sha256'}
            or type(marker['version']) is not int or marker['version'] != 1
            or marker['world_id'] != cfg.world_id or type(marker['generation']) is not int or marker['generation'] != generation
            or not isinstance(marker['file'], str) or not re.fullmatch(r'snapshot-[0-9a-f]{32}\.json', marker['file'])
            or type(marker['length']) is not int or not 0 < marker['length'] <= 64 * 1024 * 1024
            or not isinstance(marker['sha256'], str) or not re.fullmatch(r'[0-9a-f]{64}', marker['sha256'])): return False
        snapshot = read_file(checkpoint, marker['file'], 64 * 1024 * 1024)
        if len(snapshot) != marker['length'] or hashlib.sha256(snapshot).hexdigest() != marker['sha256']: return False
        save_dir = worldfd
        for part in parts[:-1]: save_dir = open_dir(save_dir, part)
        if read_file(save_dir, parts[-1], 64 * 1024 * 1024) != snapshot: return False
        data = json.loads(snapshot)
        if (not isinstance(data, dict) or not isinstance(data.get('world'), dict)
            or not isinstance(data.get('multiplayer'), dict) or not isinstance(data.get('player'), dict)
            or not isinstance(data['player'].get('scene'), str) or not data['player']['scene'].strip()
            or data.get('official_house_outbox') != 'null'): return False
        world = data.get('world_generation')
        if not isinstance(world, dict) or not isinstance(world.get('official_map'), dict): return False
        map_data = world['official_map']
        if map_data.get('world_id') != cfg.world_id or not _same_json(map_data.get('generation'), generation): return False
        return read_file(checkpoint, 'current.json', 4096) == marker_bytes
    except (OSError, ValueError, TypeError, json.JSONDecodeError, OverflowError):
        return False
    finally:
        for fd in reversed(opened): os.close(fd)


def _validated_running_checkpoint(cfg, generation, service_owner, save_path):
    # Startup checks run while the world owns the exclusive lease. Every file is
    # read with no-follow and stable inode/size/mtime checks.
    return _validated_checkpoint(cfg, generation, service_owner, save_path, require_lock=False)


def _start_ready(cfg, system, db, job, generation, boot_nonce, owner, proof_path, proof, receipt, pid, mode, checkpoint_protocol=False, bootstrap_protocol=False):
    if mode not in ('fresh', 'restore'): return False
    # systemd can report the forked main PID before exec has applied the unit's
    # privilege restrictions. Treat that transient state as not ready; the
    # caller retries under a bounded deadline and still verifies the final PID.
    try:
        if (not system.active() or system.pid() != pid or system.credentials(pid) != owner
            or not _verify_start_proof(proof_path, proof, owner)): return False
    except (OSError, RuntimeError):
        return False
    try: fd = os.open(receipt, os.O_RDONLY | os.O_NOFOLLOW)
    except FileNotFoundError: return False
    try:
        info = os.fstat(fd)
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != owner[0] or info.st_size <= 0
            or info.st_size > 4096 or info.st_mode & 0o022): return False
        with os.fdopen(fd, 'rb', closefd=False) as stream: actual = json.load(stream)
        current = os.stat(receipt, follow_symlinks=False)
        if (info.st_dev, info.st_ino, info.st_mtime_ns, info.st_size) != (current.st_dev, current.st_ino, current.st_mtime_ns, current.st_size): return False
    finally: os.close(fd)
    expected = {'nonce': boot_nonce, 'world_id': cfg.world_id, 'generation': generation,
                'world_root': str(cfg.world_root), 'pid': pid, 'job_id': job, 'mode': mode}
    if not isinstance(actual, dict) or set(actual) != set(expected) | {'save_path'}: return False
    if any(not _same_json(actual.get(key), value) for key, value in expected.items()): return False
    if mode == 'fresh' and set(_save_stamps(cfg.world_root)) != {actual['save_path']}: return False
    world = db.execute('SELECT status,generation,boot_nonce FROM worlds WHERE id=?', (cfg.world_id,)).fetchone()
    table = 'world_bootstrap_jobs' if bootstrap_protocol else 'world_reset_jobs'
    phase = db.execute(f'SELECT phase FROM {table} WHERE id=? AND world_id=?', (job, cfg.world_id)).fetchone()
    if world != ('closed', generation, boot_nonce) or phase != (('starting' if mode == 'fresh' else 'recovering'),): return False
    authorization = db.execute('SELECT job_id,node_id,generation,boot_nonce,mode FROM world_start_authorizations WHERE world_id=?', (cfg.world_id,)).fetchone()
    if authorization != (job, cfg.node_id, generation, boot_nonce, 'bootstrap' if bootstrap_protocol else mode): return False
    heartbeat = db.execute('SELECT boot_nonce,generation,last_seen FROM node_monitor_state WHERE node_id=? AND world_id=?',
                           (cfg.node_id, cfg.world_id)).fetchone()
    now = int(time.time())
    if not heartbeat or heartbeat[:2] != (boot_nonce, generation) or not 0 <= now-heartbeat[2] <= 90: return False
    # The old selection schema is not yet connected to real map coordinates.
    if mode == 'fresh' and db.execute('SELECT COUNT(*) FROM assignments WHERE world_id=?', (cfg.world_id,)).fetchone()[0] != 0: return False
    if (mode == 'fresh' and db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='plot_owners'").fetchone()
        and db.execute('SELECT COUNT(*) FROM plot_owners WHERE world_id=?', (cfg.world_id,)).fetchone()[0] != 0): return False
    if not _validated_registered_map(cfg, db, generation, actual['save_path'], owner): return False
    # The running service holds the checkpoint lock. Its startup receipt proves
    # it committed the snapshot; post-stop validation takes the lock itself.
    if checkpoint_protocol:
        return _validated_running_checkpoint(cfg, generation, owner, actual['save_path'])
    return True


def _backup_db(db, destination):
    fd = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    os.close(fd)
    target = sqlite3.connect(destination)
    try:
        db.backup(target)
        if target.execute('PRAGMA quick_check').fetchone() != ('ok',): raise RuntimeError('数据库备份检查失败')
    finally: target.close()
    fd = os.open(destination, os.O_RDONLY | os.O_NOFOLLOW)
    try: os.fsync(fd)
    finally: os.close(fd)
    _fsync_dir(destination.parent)


def _world_protocol_ready():
    # Startup proof, checkpoints and event reconciliation are integrated.
    # Per-job checks below still reject stale, pending or invalid state.
    return True


def execute_job(cfg, system, db, job, wait_seconds=90, checkpoint_protocol=False):
    """单次任务；异常时保留数据和关闭状态，绝不还原旧数据库。"""
    phase = 'queued'
    started_new = False
    try:
        if not re.fullmatch(r'[0-9a-f]{32}', job): raise RuntimeError('重置任务 ID 不正确')
        if cfg.map_protocol != 1: raise RuntimeError('世界地图协议尚未配置，拒绝停止旧世界')
        if [r[1] for r in db.execute('PRAGMA table_info(official_world_maps)')] != ['world_id', 'generation', 'map_json']:
            raise RuntimeError('控制服务尚不支持世界地图，拒绝停止旧世界')
        if [r[1] for r in db.execute('PRAGMA table_info(world_start_authorizations)')] != ['world_id', 'job_id', 'node_id', 'generation', 'boot_nonce', 'mode']:
            raise RuntimeError('控制服务尚不支持启动身份，拒绝停止旧世界')
        _root_marker(cfg.world_root, cfg.world_id)
        if not checkpoint_protocol and _has_checkpoint_artifacts(cfg.world_root):
            raise RuntimeError('世界检查点协议尚未接入，拒绝停止旧世界')
        if checkpoint_protocol and (_has_checkpoint_artifacts(cfg.world_root, ignore_reserved=True)
                                    or not (cfg.world_root / 'official-checkpoints').is_dir()):
            raise RuntimeError('世界检查点目录不可用')
        if not _world_protocol_ready():
            raise RuntimeError('世界服检查点、启动证明与事件对账尚未接入，拒绝停止旧世界')
        if not system.active(): raise RuntimeError('世界服务不在运行')
        pid = system.pid()
        owner = system.credentials(pid)
        _proof_directory(cfg, owner)
        before = _save_stamps(cfg.world_root)
        before_marker = None
        if checkpoint_protocol:
            marker = (cfg.world_root / 'official-checkpoints' / 'current.json').lstat()
            if not stat.S_ISREG(marker.st_mode): raise RuntimeError('世界检查点标记不可用')
            before_marker = (marker.st_dev, marker.st_ino, marker.st_size, marker.st_mtime_ns)
        challenge = {'nonce': secrets.token_hex(16), 'pid': pid, 'world_id': cfg.world_id, 'world_root': str(cfg.world_root)}
        receipt = cfg.receipt_dir / 'receipt.json'
        try: receipt.unlink()
        except FileNotFoundError: pass
        _atomic_json(cfg.receipt_dir, 'request.json', challenge, owner)
        _phase(db, job, phase, 'stopping'); phase = 'stopping'
        system.stop()
        if receipt.is_symlink() or not receipt.is_file(): raise RuntimeError('没有收到保存完成凭证')
        if json.loads(receipt.read_text(encoding='utf-8')) != challenge: raise RuntimeError('保存完成凭证不匹配')
        if not _validated_save(cfg.world_root, before): raise RuntimeError('没有找到本次成功写入的有效存档')
        if checkpoint_protocol:
            after = _save_stamps(cfg.world_root)
            changed = [path for path, stamp in after.items() if before.get(path) != stamp]
            marker = (cfg.world_root / 'official-checkpoints' / 'current.json').lstat()
            marker_stamp = (marker.st_dev, marker.st_ino, marker.st_size, marker.st_mtime_ns)
            generation = db.execute('SELECT generation FROM worlds WHERE id=?', (cfg.world_id,)).fetchone()
            if (len(changed) != 1 or marker_stamp == before_marker or generation is None
                or not _validated_checkpoint(cfg, generation[0], owner, changed[0])
                or not _validated_registered_map(cfg, db, generation[0], changed[0], owner)):
                raise RuntimeError('停机检查点无效或仍有待处理房屋事件')
        elif _has_checkpoint_artifacts(cfg.world_root): raise RuntimeError('停服时出现世界检查点，保留原目录')
        _root_marker(cfg.world_root, cfg.world_id)
        if (cfg.world_root / 'steam').exists(): raise RuntimeError('发现 Steam 存储，无法确认世界数据范围')
        _sync_tree(cfg.world_root)
        _phase(db, job, phase, 'backing_up'); phase = 'backing_up'
        jobdir = cfg.backup_dir / job
        jobdir.mkdir(mode=0o700)
        _fsync_dir(cfg.backup_dir)
        _backup_db(db, jobdir / 'control.sqlite')
        os.rename(cfg.world_root, jobdir / 'world')
        _fsync_dir(cfg.world_root.parent)
        _fsync_dir(jobdir)
        _copy_non_world(jobdir / 'world', cfg.world_root)
        _fsync_dir(cfg.world_root.parent)
        boot_nonce = secrets.token_hex(16)
        db.execute('BEGIN IMMEDIATE')
        try:
            updated = db.execute("UPDATE worlds SET generation=generation+1,boot_nonce=? WHERE id=? AND status='closed' AND generation < 9007199254740991", (boot_nonce, cfg.world_id))
            if updated.rowcount != 1: raise RuntimeError('世界状态不允许切换世代')
            db.execute('DELETE FROM assignments WHERE world_id=?', (cfg.world_id,))
            for table in ('plot_owners', 'plot_capture_events', 'plot_houses', 'plot_house_history', 'official_world_maps', 'world_entry_readiness'):
                if db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (table,)).fetchone():
                    db.execute(f'DELETE FROM {table} WHERE world_id=?', (cfg.world_id,))
            db.execute('DELETE FROM node_online WHERE node_id IN (SELECT node_id FROM node_monitor_state WHERE world_id=?)', (cfg.world_id,))
            db.execute('DELETE FROM node_monitor_state WHERE world_id=?', (cfg.world_id,))
            _phase(db, job, phase, 'starting')
            generation = db.execute('SELECT generation FROM worlds WHERE id=?', (cfg.world_id,)).fetchone()[0]
            db.execute('INSERT OR REPLACE INTO world_start_authorizations VALUES(?,?,?,?,?,?)',
                       (cfg.world_id, job, cfg.node_id, generation, boot_nonce, 'fresh'))
            db.execute('COMMIT')
            phase = 'starting'
        except Exception:
            db.execute('ROLLBACK')
            raise
        generation = db.execute('SELECT generation FROM worlds WHERE id=?', (cfg.world_id,)).fetchone()[0]
        start_challenge = {'nonce': boot_nonce, 'world_id': cfg.world_id, 'generation': generation, 'world_root': str(cfg.world_root)}
        proof_path, proof = _write_start_proof(cfg, job, generation, boot_nonce, 'fresh', owner)
        start_receipt = cfg.receipt_dir / 'start-receipt.json'
        try: start_receipt.unlink()
        except FileNotFoundError: pass
        _atomic_json(cfg.receipt_dir, 'start-request.json', start_challenge, owner)
        started_new = True
        system.start()
        new_pid = system.pid()
        if new_pid == pid: raise RuntimeError('新世界进程 PID 未更新')
        deadline = time.monotonic() + wait_seconds
        while time.monotonic() < deadline:
            if _start_ready(cfg, system, db, job, generation, boot_nonce, owner, proof_path, proof, start_receipt, new_pid, 'fresh', checkpoint_protocol): break
            time.sleep(.25)
        else: raise RuntimeError('新世界没有在时限内确认启动')
        db.execute('BEGIN IMMEDIATE')
        try:
            if not _start_ready(cfg, system, db, job, generation, boot_nonce, owner, proof_path, proof, start_receipt, new_pid, 'fresh', checkpoint_protocol):
                raise RuntimeError('开放前世界地图或启动身份发生变化')
            _phase(db, job, phase, 'complete')
            opened = db.execute("UPDATE worlds SET status='open' WHERE id=? AND status='closed' AND generation=? AND boot_nonce=?", (cfg.world_id, generation, boot_nonce))
            if opened.rowcount != 1: raise RuntimeError('世界状态不允许开放')
            db.execute('INSERT INTO audit(action,target,at) VALUES(?,?,?)', ('world_reset_complete', cfg.world_id + '/' + job, int(time.time())))
            db.execute('COMMIT')
            phase = 'complete'
        except Exception:
            db.execute('ROLLBACK')
            raise
        return True
    except Exception as exc:
        if started_new:
            try: system.stop()
            except Exception: pass
        # 错误文字不含路径或命令，后台只显示受控通用提示。
        try: _phase(db, job, phase, 'failed', '重置未完成，世界保持关闭；请检查本机服务日志和备份。')
        except Exception: pass
        print('[reset] 任务失败：', type(exc).__name__, str(exc), flush=True)
        return False


def recover_job(cfg, system, db, job, wait_seconds=90):
    """Restore a stopped world's verified backup into a strictly newer generation."""
    if not re.fullmatch(r'[0-9a-f]{32}', job): raise ValueError('重置任务 ID 不正确')
    if cfg.map_protocol != 1 or not _world_protocol_ready():
        raise RuntimeError('世界检查点和事件对账协议尚未接入，保留备份并拒绝自动恢复')
    jobdir = cfg.backup_dir / job
    if jobdir.is_symlink() or not jobdir.is_dir() or jobdir.resolve().parent != cfg.backup_dir:
        raise ValueError('备份目录无效')
    if system.active(): raise RuntimeError('必须先停掉世界服务')
    row = db.execute('SELECT phase FROM world_reset_jobs WHERE id=? AND world_id=?', (job, cfg.world_id)).fetchone()
    world = db.execute('SELECT status,generation FROM worlds WHERE id=?', (cfg.world_id,)).fetchone()
    if row != ('failed',) or not world or world[0] != 'closed' or world[1] >= 9007199254740991:
        raise RuntimeError('只有关闭世界的失败任务可以恢复')
    import migrate
    import restore_stage
    source = jobdir / 'world'
    if source.is_symlink() or not source.is_dir(): raise RuntimeError('备份世界目录无效')
    source_owner = source.stat()
    if source_owner.st_uid == 0: raise RuntimeError('备份世界不得由 root 运行')
    migrate.assert_no_world_user_processes(source_owner.st_uid)
    stage = jobdir / 'recovery-stage'
    displaced = jobdir / 'failed-new-world'
    if stage.exists() or displaced.exists(): raise RuntimeError('发现先前恢复痕迹，须人工检查')
    migrated = restore_stage.prepare(cfg, jobdir, stage, world[1] + 1)
    owner_info = (stage / 'world').stat()
    owner = (owner_info.st_uid, owner_info.st_gid)
    # Validate the exact staged snapshot using the approved game release in an
    # isolated, non-root process before moving either production directory.
    import recovery_preflight
    stage_manifest, release_manifest = recovery_preflight.verify_production(
        stage, cfg, migrated['generation'], migrated['save_path'], owner)
    _proof_directory(cfg, owner)
    if system.active(): raise RuntimeError('准备恢复时世界服务已启动')
    migrate.assert_no_world_user_processes(owner[0])
    # The preflight rejected WAL/journal sidecars after the writer closed. An
    # immutable read cannot create SHM files or change the approved stage.
    staged_db = sqlite3.connect(f'file:{stage / "control.sqlite"}?mode=ro&immutable=1', uri=True)
    started = False
    phase = 'failed'
    try:
        tables = ('assignments','plot_owners','plot_houses','plot_house_history',
                  'plot_capture_events','official_world_maps','plot_snapshot_revisions')
        copied = {}
        for table in tables:
            columns = [r[1] for r in db.execute(f'PRAGMA table_info({table})')]
            if not columns or columns != [r[1] for r in staged_db.execute(f'PRAGMA table_info({table})')]:
                raise RuntimeError('控制数据库表结构已变化，不能恢复')
            copied[table] = (columns, staged_db.execute(f'SELECT * FROM {table} WHERE world_id=?', (cfg.world_id,)).fetchall())
        recovery_preflight.verify_private_stage(stage)
        if (restore_stage._manifest(stage) != stage_manifest
            or recovery_preflight.approved_release() != release_manifest):
            raise RuntimeError('恢复暂存数据在预演后发生变化')
        if system.active(): raise RuntimeError('准备恢复时世界服务已启动')
        migrate.assert_no_world_user_processes(owner[0])
        if cfg.world_root.exists():
            if cfg.world_root.is_symlink() or not cfg.world_root.is_dir(): raise RuntimeError('当前世界目录无效')
            _root_marker(cfg.world_root, cfg.world_id)
            os.rename(cfg.world_root, displaced)
            _fsync_dir(cfg.world_root.parent); _fsync_dir(jobdir)
        os.rename(stage / 'world', cfg.world_root)
        _fsync_dir(cfg.world_root.parent); _fsync_dir(stage)
        boot_nonce = secrets.token_hex(16)
        db.execute('BEGIN IMMEDIATE')
        try:
            check = db.execute('SELECT status,generation FROM worlds WHERE id=?', (cfg.world_id,)).fetchone()
            if check != world: raise RuntimeError('恢复时世界状态发生变化')
            _phase(db, job, phase, 'recovering')
            changed = db.execute("UPDATE worlds SET generation=?,boot_nonce=? WHERE id=? AND status='closed' AND generation=?",
                                 (migrated['generation'], boot_nonce, cfg.world_id, world[1]))
            if changed.rowcount != 1: raise RuntimeError('恢复世代无法更新')
            for table in tables:
                columns, rows = copied[table]
                db.execute(f'DELETE FROM {table} WHERE world_id=?', (cfg.world_id,))
                if rows: db.executemany(f'INSERT INTO {table} VALUES(' + ','.join('?' for _ in columns) + ')', rows)
            db.execute('DELETE FROM node_online WHERE node_id IN (SELECT node_id FROM node_monitor_state WHERE world_id=?)', (cfg.world_id,))
            db.execute('DELETE FROM node_monitor_state WHERE world_id=?', (cfg.world_id,))
            db.execute('DELETE FROM world_entry_readiness WHERE world_id=?', (cfg.world_id,))
            db.execute('INSERT OR REPLACE INTO world_start_authorizations VALUES(?,?,?,?,?,?)',
                       (cfg.world_id, job, cfg.node_id, migrated['generation'], boot_nonce, 'restore'))
            db.execute('COMMIT'); phase = 'recovering'
        except Exception:
            db.execute('ROLLBACK'); raise
        proof_path, proof = _write_start_proof(cfg, job, migrated['generation'], boot_nonce, 'restore', owner)
        receipt = cfg.receipt_dir / 'start-receipt.json'
        try: receipt.unlink()
        except FileNotFoundError: pass
        challenge = {'nonce':boot_nonce,'world_id':cfg.world_id,'generation':migrated['generation'],'world_root':str(cfg.world_root)}
        _atomic_json(cfg.receipt_dir, 'start-request.json', challenge, owner)
        migrate.assert_no_world_user_processes(owner[0])
        started = True
        system.start()
        pid = system.pid()
        deadline = time.monotonic() + wait_seconds
        while time.monotonic() < deadline:
            if _start_ready(cfg, system, db, job, migrated['generation'], boot_nonce, owner, proof_path, proof, receipt, pid, 'restore', True): break
            time.sleep(.25)
        else: raise RuntimeError('恢复世界没有在时限内确认启动')
        db.execute('BEGIN IMMEDIATE')
        try:
            if not _start_ready(cfg, system, db, job, migrated['generation'], boot_nonce, owner, proof_path, proof, receipt, pid, 'restore', True):
                raise RuntimeError('开放前恢复世界身份发生变化')
            _phase(db, job, phase, 'recovered')
            changed = db.execute("UPDATE worlds SET status='open' WHERE id=? AND status='closed' AND generation=? AND boot_nonce=?",
                                 (cfg.world_id,migrated['generation'],boot_nonce))
            if changed.rowcount != 1: raise RuntimeError('恢复世界状态不允许开放')
            db.execute('INSERT INTO audit(action,target,at) VALUES(?,?,?)', ('world_reset_recovered',cfg.world_id+'/'+job,int(time.time())))
            db.execute('COMMIT'); phase = 'recovered'
        except Exception:
            db.execute('ROLLBACK'); raise
        return True
    except Exception:
        if phase == 'recovering':
            try:
                db.execute("UPDATE worlds SET status='closed' WHERE id=?", (cfg.world_id,))
                _phase(db, job, phase, 'failed', '恢复未完成，世界保持关闭；请检查备份及本机日志。')
            except Exception: pass
        if started:
            try: system.stop()
            except Exception: pass
        raise
    finally:
        staged_db.close()


def tick(cfg, system, db):
    # Never advertise an operational worker while the production protocol gate
    # is closed. This also removes a heartbeat left by an older worker process.
    if not _world_protocol_ready():
        db.execute('DELETE FROM world_reset_worker WHERE world_id=?', (cfg.world_id,))
        return
    now = int(time.time())
    db.execute('INSERT INTO world_reset_worker(world_id,last_seen,protocol_version) VALUES(?,?,1) ON CONFLICT(world_id) DO UPDATE SET last_seen=excluded.last_seen,protocol_version=1', (cfg.world_id, now))
    interrupted = db.execute("SELECT id,phase FROM world_reset_jobs WHERE world_id=? AND phase NOT IN ('queued','complete','recovered','failed') ORDER BY created LIMIT 1", (cfg.world_id,)).fetchone()
    if interrupted:
        # 启动后的未知进程可能持有尚未保存的数据；由管理员检查，不主动停服。
        _phase(db, interrupted[0], interrupted[1], 'failed', '重置中断，世界保持关闭；请检查本机服务日志和备份。')
    else:
        job = db.execute("SELECT id FROM world_reset_jobs WHERE world_id=? AND phase='queued' ORDER BY created LIMIT 1", (cfg.world_id,)).fetchone()
        if job: execute_job(cfg, system, db, job[0], checkpoint_protocol=True)


def run(cfg, system):
    with _connect(cfg) as db:
        while True:
            tick(cfg, system, db)
            time.sleep(5)


def main():
    if os.geteuid() != 0 or not (len(sys.argv) == 1 or
        (len(sys.argv) == 3 and sys.argv[1] == '--recover' and re.fullmatch(r'[0-9a-f]{32}', sys.argv[2]))):
        raise SystemExit('只能由 root 按固定配置运行；离线恢复只接受 --recover 和有效任务 ID')
    cfg = load_config()
    lock_path = CONFIG.parent / 'worker.lock'
    fd = os.open(lock_path, os.O_WRONLY | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, 'w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if len(sys.argv) == 3:
            with _connect(cfg) as db: recover_job(cfg, Systemd(), db, sys.argv[2])
        else: run(cfg, Systemd())


if __name__ == '__main__': main()
