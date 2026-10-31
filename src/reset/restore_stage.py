#!/usr/bin/python3
"""Prepare a verified, same-world recovery copy without changing source data.

This module intentionally does not install the result or start a service.
Only root-owned offline callers may use it as a component of recovery.
"""
import copy
import configparser
from contextlib import closing
import dataclasses
import hashlib
import json
import math
import os
import re
import secrets
import shutil
import sqlite3
import stat
from pathlib import Path
import worker

_TABLES = ('assignments', 'plot_owners', 'plot_houses', 'plot_house_history',
           'plot_capture_events', 'plot_snapshot_revisions')


def _manifest(root):
    result = {}
    for base, directories, files in os.walk(root, followlinks=False):
        for name in sorted(directories + files):
            path = Path(base) / name
            info = path.lstat()
            relative = str(path.relative_to(root))
            if stat.S_ISDIR(info.st_mode):
                result[relative] = ('dir', stat.S_IMODE(info.st_mode), info.st_uid, info.st_gid)
            elif stat.S_ISREG(info.st_mode) and info.st_nlink == 1:
                with path.open('rb') as stream:
                    checksum = hashlib.sha256()
                    for chunk in iter(lambda: stream.read(1024 * 1024), b''): checksum.update(chunk)
                    digest = checksum.hexdigest()
                result[relative] = ('file', stat.S_IMODE(info.st_mode), info.st_uid, info.st_gid, info.st_size, digest)
            else:
                raise RuntimeError('备份包含符号链接或不支持的文件')
    return result


def _map_valid(value, world, generation):
    if not isinstance(value, dict) or value.get('world_id') != world or value.get('generation') != generation:
        return False
    if type(value.get('version')) is not int or value['version'] != 1: return False
    if type(value.get('seed')) is not int or not -(2**31) <= value['seed'] < 2**31 or value['seed'] == 0 \
        or type(value.get('terrain_version')) is not int or not 0 < value['terrain_version'] < 2**31: return False
    scale = value.get('biome_scale')
    if type(scale) not in (int,float) or not math.isfinite(scale) or not 0 < scale <= 100: return False
    for x,y in (('town_x','town_y'),('home_x','home_y')):
        if type(value.get(x)) is not int or type(value.get(y)) is not int \
            or not -4096 <= value[x] < 4096 or not -2048 <= value[y] < 2048: return False
    plots, spawns = value.get('plots'), value.get('spawns')
    if not isinstance(plots, list) or len(plots) != 8 or not isinstance(spawns, list) or len(spawns) != 4: return False
    if {p.get('id') for p in plots if isinstance(p, dict)} != {f'p{i}' for i in range(1, 9)}: return False
    if {p.get('id') for p in spawns if isinstance(p, dict)} != {'s_town','s_east','s_west','s_north'}: return False
    for p in plots:
        if any(type(p.get(k)) is not int for k in ('x','y','w','h')) or p['w'] != 32 or p['h'] != 32: return False
        if not -4096 <= p['x'] <= 4064 or not -2048 <= p['y'] <= 2016: return False
    for i, a in enumerate(plots):
        for b in plots[i+1:]:
            if a['x']-24 < b['x']+32 and b['x'] < a['x']+56 and a['y']-24 < b['y']+32 and b['y'] < a['y']+56:
                return False
    for s in spawns:
        if any(type(s.get(k)) is not int for k in ('x','y')) or not -4096 < s['x'] < 4095 or not -2048 < s['y'] < 2047: return False
        if any(p['x'] < s['x']+2 and s['x']-1 < p['x']+32 and p['y'] < s['y']+2 and s['y']-1 < p['y']+32 for p in plots): return False
    return True


def _room_keys(snapshot, world, generation, house_rows):
    """Reject stale/ambiguous room snapshots before a backup can be used."""
    rooms = snapshot.get('world')
    if not isinstance(rooms, dict): raise RuntimeError('备份世界数据无效')
    houses = {row[0]: row for row in house_rows}
    found_rooms = []
    for key, value in rooms.items():
        if not isinstance(key, str): raise RuntimeError('备份场景键无效')
        if not key.startswith('official-room'): continue
        match = re.fullmatch(r'official-room/([^/]+)/([1-9][0-9]*)/(p[1-8])/([0-9a-f]{32})/([1-9][0-9]*)', key)
        if not match: raise RuntimeError('备份室内键无效')
        room_world, room_gen, plot, house, version = match.groups()
        row = houses.get(plot)
        if room_world != world or int(room_gen) != generation or row is None or row[1] != house or row[2] != int(version) or row[3] != 'intact':
            raise RuntimeError('备份室内房屋或世代不一致')
        if not isinstance(value, dict) or value.get('position_space') != 'scene_local_v1' or not isinstance(value.get('entities'), list) or value.get('tiles') != [] or value.get('roads') != []:
            raise RuntimeError('备份室内快照无效')
        found_rooms.append(key)
    if found_rooms:
        ids = set()
        for value in rooms.values():
            if not isinstance(value, dict) or not isinstance(value.get('entities'), list): raise RuntimeError('备份实体格式无效')
            for entity in value['entities']:
                if not isinstance(entity, dict) or not isinstance(entity.get('id'), str) or not entity['id'] or entity['id'] in ids:
                    raise RuntimeError('备份实体身份重复或无效')
                ids.add(entity['id'])
    return found_rooms


def _rekey_rooms(snapshot, world, old_generation, new_generation, house_rows):
    result = copy.deepcopy(snapshot)
    keys = _room_keys(result, world, old_generation, house_rows)
    rooms = result['world']
    for key in keys:
        parts = key.split('/'); parts[2] = str(new_generation); new_key = '/'.join(parts)
        if new_key in rooms: raise RuntimeError('恢复室内键冲突')
        rooms[new_key] = rooms.pop(key)
    return result


def _check_world(db, world, generation, snapshot, registered):
    if not _map_valid(registered, world, generation): raise RuntimeError('备份地图无效')
    if not worker._same_world_map(snapshot.get('world_generation', {}).get('official_map'), registered):
        raise RuntimeError('备份存档和控制地图不一致')
    if snapshot.get('official_house_outbox') != 'null': raise RuntimeError('备份有待处理房屋事件')
    if not isinstance(snapshot.get('world'), dict) or not isinstance(snapshot.get('multiplayer'), dict):
        raise RuntimeError('备份缺少玩家或世界数据')
    plots = {p['id']: p for p in registered['plots']}
    assignments = db.execute('SELECT steam_id,plot_id,spawn_id,house_x,house_y,map_generation FROM assignments WHERE world_id=?', (world,)).fetchall()
    by_player = {r[0]: r for r in assignments}
    if len(by_player) != len(assignments): raise RuntimeError('玩家地块分配重复')
    spawn_ids = {s['id'] for s in registered['spawns']}
    for sid, plot, spawn, x, y, gen in assignments:
        p = plots.get(plot)
        if (not isinstance(sid, str) or not re.fullmatch(r'[0-9]{17}', sid) or p is None or spawn not in spawn_ids
            or type(x) is not int or type(y) is not int or gen != generation
            or not p['x'] <= x < p['x']+32 or not p['y'] <= y < p['y']+32): raise RuntimeError('备份玩家分配无效')
    for key, player in snapshot['multiplayer'].items():
        if not isinstance(key, str) or not key.startswith('steam:') or key[6:] not in by_player or not isinstance(player, dict):
            raise RuntimeError('备份玩家存档身份无效')
    owners = db.execute('SELECT plot_id,steam_id FROM plot_owners WHERE world_id=?', (world,)).fetchall()
    by_plot = dict(owners)
    if len(by_plot) != len(owners) or any(plot not in plots or sid not in by_player for plot, sid in owners):
        raise RuntimeError('备份地块归属无效')
    if any(plot not in by_plot for _, plot, *_ in assignments): raise RuntimeError('分配地块缺少归属')
    entities = {}
    for scene_path, scene in snapshot['world'].items():
        if not isinstance(scene, dict) or not isinstance(scene.get('entities'), list): raise RuntimeError('备份实体格式无效')
        for entity in scene['entities']:
            if not isinstance(entity, dict) or not isinstance(entity.get('id'), str): raise RuntimeError('备份实体身份无效')
            entities.setdefault(entity['id'], []).append((scene_path, entity))
    houses = db.execute('SELECT plot_id,house_id,house_version,state,blueprint_id,house_x,house_y FROM plot_houses WHERE world_id=?', (world,)).fetchall()
    _room_keys(snapshot, world, generation, houses)
    if len(houses) > len(owners) or len({row[0] for row in houses}) != len(houses):
        raise RuntimeError('地块房屋记录无效')
    unbuilt = set(by_plot) - {row[0] for row in houses}
    for plot in unbuilt:
        if db.execute('SELECT COUNT(*) FROM plot_house_history WHERE world_id=? AND plot_id=?', (world,plot)).fetchone()[0]:
            raise RuntimeError('未建房地块存在历史记录')
        bounds = plots[plot]
        farm = snapshot['world'].get('res://Scence/Farm.tscn')
        if not isinstance(farm, dict) or not isinstance(farm.get('entities'), list):
            raise RuntimeError('未建房地块缺少农场实体快照')
        for entity in farm.get('entities', []):
            x, y = entity.get('cell_x'), entity.get('cell_y')
            if (entity.get('building') == 'fangzi' or entity.get('blueprint') == 'fangzi') and type(x) in (int,float) and type(y) in (int,float) \
                and bounds['x'] <= x < bounds['x']+32 and bounds['y'] <= y < bounds['y']+32:
                raise RuntimeError('未建房地块存在未登记房屋')
    for plot, house, version, state, blueprint, x, y in houses:
        if plot not in by_plot or not re.fullmatch(r'[0-9a-f]{32}', house or '') or type(version) is not int or version <= 0:
            raise RuntimeError('备份房屋记录无效')
        if db.execute('SELECT COUNT(*) FROM plot_house_history WHERE world_id=? AND plot_id=? AND house_id=? AND house_version=? AND blueprint_id=?',
                      (world,plot,house,version,blueprint)).fetchone()[0] != 1: raise RuntimeError('备份房屋历史不一致')
        ref = house if state == 'intact' else blueprint
        found = entities.get(ref, [])
        if len(found) != 1 or found[0][0] != 'res://Scence/Farm.tscn':
            raise RuntimeError('备份房屋不在农场或身份重复')
        matches = [found[0][1]]
        if matches[0].get('layer',0) != 0: raise RuntimeError('备份房屋不在地面层')
        if matches[0].get('cell_x') != x or matches[0].get('cell_y') != y:
            raise RuntimeError('备份房屋实体不一致')
        if state == 'intact':
            if (blueprint != '' or matches[0].get('type') != 'building'
                or matches[0].get('building') != 'fangzi' or 'blueprint' in matches[0]):
                raise RuntimeError('备份房屋类型不一致')
        elif state in ('destroyed','claimed'):
            if (not re.fullmatch(r'[0-9a-f]{32}', blueprint or '') or matches[0].get('type') != 'blueprint'
                or 'building' in matches[0] or matches[0].get('blueprint') != 'fangzi'
                or matches[0].get('official_origin_house_id') != house): raise RuntimeError('备份房屋蓝图不一致')
        else: raise RuntimeError('备份房屋状态无效')
        selected = [a for a in assignments if a[1] == plot]
        if not selected or any(a[3:5] != (x,y) for a in selected): raise RuntimeError('备份房屋分配位置不一致')
    for key, gen, house, version, blueprint in db.execute('SELECT event_id,generation,house_id,house_version,blueprint_id FROM plot_capture_events WHERE world_id=?', (world,)):
        if (not re.fullmatch(r'[0-9a-f]{32}', key or '') or gen != generation
            or db.execute('SELECT COUNT(*) FROM plot_house_history WHERE world_id=? AND house_id=? AND house_version=? AND blueprint_id=?',
                          (world,house,version,blueprint)).fetchone()[0] != 1): raise RuntimeError('备份占领事件不一致')


def _write_atomic(path, data, owner, mode):
    temp = path.parent / ('.recover-' + secrets.token_hex(16))
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
    try:
        os.fchown(fd, *owner); os.fchmod(fd, mode)
        with os.fdopen(fd, 'wb') as stream: stream.write(data); stream.flush(); os.fsync(stream.fileno())
        os.replace(temp, path); worker._fsync_dir(path.parent)
    finally:
        try: temp.unlink()
        except FileNotFoundError: pass


def prepare(cfg, backup, destination, new_generation):
    """Build a verifiable recovery copy; caller must guarantee all services stopped."""
    backup, destination = Path(backup), Path(destination)
    source, database = backup/'world', backup/'control.sqlite'
    if (backup.is_symlink() or not backup.is_dir() or source.is_symlink() or not source.is_dir()
        or database.is_symlink() or not database.is_file() or destination.exists() or destination.is_symlink()
        or destination.parent.resolve() != backup.resolve()): raise RuntimeError('恢复暂存目录无效')
    if any(database.with_name(database.name + suffix).exists() or database.with_name(database.name + suffix).is_symlink()
           for suffix in ('-wal', '-shm', '-journal')):
        raise RuntimeError('备份数据库存在未核对的日志文件')
    before = _manifest(backup)
    worker._root_marker(source, cfg.world_id)
    owner_info = source.stat(); owner = (owner_info.st_uid, owner_info.st_gid)
    # A completed SQLite backup is standalone. immutable avoids creating WAL
    # sidecars beside the original or consulting mutable external journals.
    with closing(sqlite3.connect('file:'+str(database)+'?mode=ro&immutable=1', uri=True)) as old:
        if old.execute('PRAGMA quick_check').fetchone() != ('ok',): raise RuntimeError('备份数据库损坏')
        row = old.execute('SELECT generation FROM worlds WHERE id=?', (cfg.world_id,)).fetchone()
        if not row or type(new_generation) is not int or not 0 < row[0] < new_generation <= 9007199254740991:
            raise RuntimeError('恢复世代必须递增')
        old_generation = row[0]
        maps = old.execute('SELECT generation,map_json FROM official_world_maps WHERE world_id=?',(cfg.world_id,)).fetchall()
        if len(maps) != 1 or maps[0][0] != old_generation: raise RuntimeError('备份地图世代不一致')
        registered = json.loads(maps[0][1])
        saves = list(worker._save_stamps(source))
        source_cfg = dataclasses.replace(cfg, world_root=source)
        if len(saves) != 1 or not worker._validated_checkpoint(source_cfg,old_generation,owner,saves[0]) \
            or not worker._validated_registered_map(source_cfg,old,old_generation,saves[0],owner):
            raise RuntimeError('备份检查点或地图校验失败')
        selected = 'legacy'
        selection = source/'saves'/'selection.cfg'
        if selection.exists():
            try:
                config = configparser.ConfigParser(interpolation=None, delimiters=('=',), strict=True)
                config.read_string(selection.read_text(encoding='utf-8'))
                if config.has_option('selection','run'):
                    selected = json.loads(config.get('selection','run',raw=True))
            except (OSError, ValueError, configparser.Error) as exc:
                raise RuntimeError('备份存档槽配置无效') from exc
        expected_slot = 'legacy' if saves[0] == 'save.now.json' else saves[0].split('/')[1]
        if not isinstance(selected,str) or selected != expected_slot:
            raise RuntimeError('备份存档槽与检查点不一致')
        snapshot = json.loads((source/saves[0]).read_bytes())
        _check_world(old,cfg.world_id,old_generation,snapshot,registered)
        destination.mkdir(mode=0o700)
        try:
            target = destination/'world'
            shutil.copytree(source,target,copy_function=shutil.copy2)
            for base, dirs, files in os.walk(target,topdown=False):
                for name in dirs+files:
                    path = Path(base)/name; original = source/path.relative_to(target); info=original.stat()
                    os.chown(path,info.st_uid,info.st_gid); os.chmod(path,stat.S_IMODE(info.st_mode))
                info=(source/Path(base).relative_to(target)).stat(); os.chown(base,info.st_uid,info.st_gid); os.chmod(base,stat.S_IMODE(info.st_mode))
            staged_db = destination/'control.sqlite'
            with closing(sqlite3.connect(staged_db)) as stage:
                old.backup(stage)
                stage.execute('BEGIN IMMEDIATE')
                changed=stage.execute("UPDATE worlds SET status='closed',generation=?,boot_nonce='' WHERE id=? AND generation=?",(new_generation,cfg.world_id,old_generation))
                if changed.rowcount != 1: raise RuntimeError('恢复世界世代不一致')
                stage.execute('UPDATE assignments SET map_generation=? WHERE world_id=? AND map_generation=?',(new_generation,cfg.world_id,old_generation))
                stage.execute('UPDATE plot_capture_events SET generation=? WHERE world_id=? AND generation=?',(new_generation,cfg.world_id,old_generation))
                registered_new=copy.deepcopy(registered); registered_new['generation']=new_generation
                stage.execute('UPDATE official_world_maps SET generation=?,map_json=? WHERE world_id=?',
                              (new_generation,json.dumps(registered_new,separators=(',',':')),cfg.world_id))
                for table in ('world_entry_readiness','world_start_authorizations'):
                    stage.execute(f'DELETE FROM {table} WHERE world_id=?',(cfg.world_id,))
                stage.commit()
                house_rows = old.execute('SELECT plot_id,house_id,house_version,state,blueprint_id,house_x,house_y FROM plot_houses WHERE world_id=?',(cfg.world_id,)).fetchall()
                migrated=_rekey_rooms(snapshot,cfg.world_id,old_generation,new_generation,house_rows); migrated['world_generation']['official_map']['generation']=new_generation
                encoded=json.dumps(migrated,ensure_ascii=False,separators=(',',':'),allow_nan=False).encode()
                save=target/saves[0]; oldmode=stat.S_IMODE(save.stat().st_mode)
                _write_atomic(save,encoded,owner,oldmode)
                checkpoint=target/'official-checkpoints'; marker=json.loads((checkpoint/'current.json').read_text())
                marker['generation']=new_generation; marker['file']='snapshot-'+secrets.token_hex(16)+'.json'
                marker['length']=len(encoded); marker['sha256']=hashlib.sha256(encoded).hexdigest()
                _write_atomic(checkpoint/marker['file'],encoded,owner,0o600)
                _write_atomic(checkpoint/'current.json',json.dumps(marker,separators=(',',':')).encode(),owner,0o600)
                target_cfg=dataclasses.replace(cfg,world_root=target)
                if not worker._validated_checkpoint(target_cfg,new_generation,owner,saves[0]) \
                    or not worker._validated_registered_map(target_cfg,stage,new_generation,saves[0],owner):
                    raise RuntimeError('恢复暂存检查点校验失败')
                _check_world(stage,cfg.world_id,new_generation,migrated,registered_new)
                restored=_rekey_rooms(migrated,cfg.world_id,new_generation,old_generation,house_rows);restored['world_generation']['official_map']['generation']=old_generation
                if restored != snapshot: raise RuntimeError('恢复修改了玩家或世界数据')
                for table in _TABLES:
                    old_rows=old.execute(f'SELECT * FROM {table} WHERE world_id=?',(cfg.world_id,)).fetchall()
                    new_rows=stage.execute(f'SELECT * FROM {table} WHERE world_id=?',(cfg.world_id,)).fetchall()
                    cols=[r[1] for r in stage.execute(f'PRAGMA table_info({table})')]
                    col='map_generation' if table=='assignments' else 'generation' if table=='plot_capture_events' else None
                    if col:
                        index=cols.index(col); new_rows=[tuple(old_generation if i==index and v==new_generation else v for i,v in enumerate(row)) for row in new_rows]
                    if sorted(old_rows,key=repr) != sorted(new_rows,key=repr): raise RuntimeError('恢复修改了地块或历史数据')
            if any(staged_db.with_name(staged_db.name + suffix).exists() or staged_db.with_name(staged_db.name + suffix).is_symlink()
                   for suffix in ('-wal', '-shm', '-journal')):
                raise RuntimeError('恢复暂存数据库日志未完成合并')
            worker._sync_tree(destination)
            worker._fsync_dir(destination)
            after = {k:v for k,v in _manifest(backup).items() if k != destination.name and not k.startswith(destination.name+'/')}
            if after != before:
                changed = sorted(k for k in before.keys() | after.keys() if before.get(k) != after.get(k))
                raise RuntimeError('原备份在恢复暂存期间发生变化：' + ','.join(changed[:8]))
            return {'old_generation':old_generation,'generation':new_generation,'save_path':saves[0]}
        except Exception:
            # A partial stage is left in place for inspection; the source is never removed.
            raise
