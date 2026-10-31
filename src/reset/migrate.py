#!/usr/bin/python3
"""一次性复制到专用 XDG 数据目录；仅接受固定路径，保留原数据。"""
import hashlib
import ctypes
import os
from pathlib import Path
import pwd
import secrets
import shutil
import stat
import sys

import worker

SOURCE = Path('/var/lib/wheatstead-world/.local/share/godot/app_userdata/Wheatstead')
DESTINATION = Path(worker.WORLD_ROOT)
BACKUPS = Path('/var/lib/wheatstead-world-migration-backups')


def manifest(root):
    entries = {}
    for parent, dirs, files in os.walk(root, followlinks=False):
        for name in sorted(dirs + files):
            path = Path(parent) / name
            info = path.lstat()
            mode = info.st_mode
            if mode & 0o022: raise RuntimeError('来源文件不应允许组用户或其他用户写入')
            key = str(path.relative_to(root))
            if stat.S_ISDIR(mode): entries[key] = ('directory',)
            elif stat.S_ISREG(mode):
                digest = hashlib.sha256()
                fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
                with os.fdopen(fd, 'rb') as src:
                    for chunk in iter(lambda: src.read(1024 * 1024), b''): digest.update(chunk)
                entries[key] = ('file', path.stat().st_size, digest.hexdigest())
            else: raise RuntimeError('来源包含符号链接或特殊文件')
    return entries


def _rename_no_replace(source, destination):
    libc = ctypes.CDLL(None, use_errno=True)
    result = libc.renameat2(-100, os.fsencode(source), -100, os.fsencode(destination), 1)
    if result:
        code = ctypes.get_errno()
        raise OSError(code, os.strerror(code))


def _copy_owned(source, target):
    shutil.copytree(source, target, copy_function=shutil.copy2)
    for parent, dirs, files in os.walk(target, topdown=False):
        for name in dirs + files:
            path = Path(parent) / name
            original = source / path.relative_to(target)
            info = original.stat(follow_symlinks=False)
            os.chown(path, info.st_uid, info.st_gid, follow_symlinks=False)
    info = source.stat()
    os.chown(target, info.st_uid, info.st_gid)
    worker._sync_tree(target)


def migrate(source, destination, backup_dir, system, uid, gid, verify_idle=lambda: None):
    if system.active(): raise RuntimeError('必须先停止世界服务')
    verify_idle()
    worker._safe_absolute(source, True)
    if destination.exists() or destination.is_symlink(): raise RuntimeError('目标已存在，拒绝覆盖')
    if source == destination or source in destination.parents or destination in source.parents:
        raise RuntimeError('来源与目标不能重叠')
    if source == backup_dir or source in backup_dir.parents or backup_dir in source.parents:
        raise RuntimeError('来源与备份不能重叠')
    if destination == backup_dir or destination in backup_dir.parents or backup_dir in destination.parents:
        raise RuntimeError('目标与备份不能重叠')
    if not destination.parent.is_dir() or not backup_dir.is_dir(): raise RuntimeError('目标和备份的父目录必须预先创建')
    if source.stat().st_uid != uid: raise RuntimeError('来源目录不属于世界服务用户')
    for parent, dirs, files in os.walk(source, followlinks=False):
        for name in dirs + files:
            if (Path(parent) / name).lstat().st_uid not in (0, uid):
                raise RuntimeError('来源包含其他用户所有的文件')
    before = manifest(source)
    if '.wheatstead-world-root' in before: raise RuntimeError('来源含有保留标记，须人工检查')
    token = secrets.token_hex(16)
    job = backup_dir / ('migration-' + token)
    stage = destination.parent / ('.wheatstead-migration-' + token)
    job.mkdir(mode=0o700)
    worker._fsync_dir(backup_dir)
    # 报错保留备份与临时目录，以便排查；绝不修改来源。
    _copy_owned(source, job / 'world')
    if manifest(job / 'world') != before: raise RuntimeError('备份与来源不一致')
    _copy_owned(source, stage)
    if manifest(stage) != before or manifest(source) != before: raise RuntimeError('复制期间来源变化或内容不一致')
    os.chown(stage, uid, gid)
    os.chmod(stage, 0o700)
    marker = stage / '.wheatstead-world-root'
    fd = os.open(marker, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, 'w') as out:
        out.write('official-1\n'); out.flush(); os.fsync(out.fileno())
    worker._fsync_dir(stage)
    if system.active(): raise RuntimeError('复制期间世界服务重新启动')
    verify_idle()
    _rename_no_replace(stage, destination)
    worker._fsync_dir(destination.parent)
    return job


def assert_no_world_user_processes(uid):
    for proc in Path('/proc').iterdir():
        if not proc.name.isdecimal(): continue
        try:
            line = next(line for line in (proc / 'status').read_text().splitlines() if line.startswith('Uid:'))
            if uid in [int(value) for value in line.split()[1:]]:
                raise RuntimeError('世界服务用户仍有运行中的进程')
        except FileNotFoundError: continue


def main():
    if os.geteuid() != 0 or len(sys.argv) != 1: raise SystemExit('只能由 root 使用固定路径运行，不接受参数')
    system = worker.Systemd()
    if system.active(): raise SystemExit('必须先停止世界服务')
    account = pwd.getpwnam('wheatstead-world')
    assert_no_world_user_processes(account.pw_uid)
    worker._safe_absolute(SOURCE, True)
    worker._safe_absolute(DESTINATION.parent, True)
    worker._safe_absolute(BACKUPS, True)
    worker._root_managed_parents(DESTINATION)
    worker._root_managed_parents(BACKUPS / 'child')
    info = BACKUPS.stat()
    if info.st_uid != 0 or info.st_mode & 0o077: raise SystemExit('迁移备份目录必须仅 root 可访问')
    result = migrate(SOURCE, DESTINATION, BACKUPS, system, account.pw_uid, account.pw_gid,
                     lambda: assert_no_world_user_processes(account.pw_uid))
    print('迁移副本和备份已验证；原目录保留。备份：', result)
    print('尚未修改 systemd；需单独验证专用 XDG_DATA_HOME 后才能切换服务。')


if __name__ == '__main__': main()
