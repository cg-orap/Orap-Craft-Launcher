export type LaunchState = 'idle' | 'starting' | 'running'
export type SyncState = 'synced' | 'syncing' | 'conflict'

export interface Instance {
  id: string
  name: string
  mc: string
  loader: 'Fabric' | 'Forge' | 'NeoForge' | 'Quilt' | 'Vanilla'
  loaderVersion: string
  mods: number
  lastPlayed: string
  cover: string
  java: string
  memory: string
}

export interface BackupEntry {
  id: string
  time: string
  instance: string
  size: string
  trigger: '自动·启动前' | '自动·定时' | '手动'
}

export interface CrashReport {
  id: string
  time: string
  instance: string
  file: string
  reason: string
}

export interface LogLine {
  id: number
  time: string
  level: 'INFO' | 'WARN' | 'ERROR' | 'DEBUG'
  source: string
  text: string
}

export const instances: Instance[] = [
  {
    id: 'bmc2',
    name: 'Better MC · BMC2',
    mc: '1.20.1',
    loader: 'Fabric',
    loaderVersion: '0.19.3',
    mods: 333,
    lastPlayed: '今天 22:38',
    cover: 'from-[#23361f] via-[#1b2a19] to-[#131a12]',
    java: 'Java 17.0.11',
    memory: '4096 MB',
  },
  {
    id: 'fo',
    name: 'Fabulously Optimized',
    mc: '1.21.1',
    loader: 'Fabric',
    loaderVersion: '0.16.9',
    mods: 47,
    lastPlayed: '昨天 19:02',
    cover: 'from-[#1f2f38] via-[#182530] to-[#121a21]',
    java: 'Java 21.0.4',
    memory: '3072 MB',
  },
  {
    id: 'neoforge',
    name: '机械动力 · 测试包',
    mc: '1.21.1',
    loader: 'NeoForge',
    loaderVersion: '21.1.264',
    mods: 128,
    lastPlayed: '3 天前',
    cover: 'from-[#2e2a38] via-[#241f2e] to-[#191521]',
    java: 'Java 21.0.4',
    memory: '6144 MB',
  },
  {
    id: 'vanilla',
    name: '原版 1.21.1',
    mc: '1.21.1',
    loader: 'Vanilla',
    loaderVersion: '—',
    mods: 0,
    lastPlayed: '上周',
    cover: 'from-[#26292f] via-[#1d2026] to-[#14171b]',
    java: 'Java 21.0.4',
    memory: '2048 MB',
  },
]

export const backups: BackupEntry[] = [
  { id: 'b1', time: '今天 22:35', instance: 'Better MC · BMC2', size: '412 MB', trigger: '自动·启动前' },
  { id: 'b2', time: '今天 18:10', instance: 'Fabulously Optimized', size: '96 MB', trigger: '自动·定时' },
  { id: 'b3', time: '昨天 21:44', instance: 'Better MC · BMC2', size: '408 MB', trigger: '自动·启动前' },
  { id: 'b4', time: '昨天 12:02', instance: '机械动力 · 测试包', size: '233 MB', trigger: '手动' },
]

export const crashes: CrashReport[] = [
  { id: 'c1', time: '今天 22:41', instance: 'Better MC · BMC2', file: 'crash-2026-10-26_22.41.07-client.txt', reason: 'MissingModsException' },
  { id: 'c2', time: '昨天 20:12', instance: 'Better MC · BMC2', file: 'crash-2026-10-25_20.12.55-client.txt', reason: 'NoSuchMethodError' },
  { id: 'c3', time: '10-24 19:30', instance: '机械动力 · 测试包', file: 'crash-2026-10-24_19.30.02-client.txt', reason: 'MixinApplyError' },
]

export const logs: LogLine[] = [
  { id: 1, time: '22:38:01.162', level: 'INFO', source: 'launcher', text: '启动 Better MC · BMC2（fabric-loader-0.19.3-1.20.1）' },
  { id: 2, time: '22:38:01.204', level: 'INFO', source: 'java', text: 'Java 17.0.11 · C:\Program Files\Java\jdk-17\bin\javaw.exe' },
  { id: 3, time: '22:38:02.318', level: 'DEBUG', source: 'verify', text: '3628 文件校验通过，复用 1.46 GB' },
  { id: 4, time: '22:38:03.902', level: 'INFO', source: 'minecraft', text: 'Loading 333 mods:' },
  { id: 5, time: '22:38:03.905', level: 'INFO', source: 'minecraft', text: '  - fabric-api 0.92.5+1.20.1' },
  { id: 6, time: '22:38:09.771', level: 'WARN', source: 'mixin', text: 'Mixin config betterend.mixins.json 覆盖了 2 个已存在的注入点' },
  { id: 7, time: '22:38:12.004', level: 'INFO', source: 'minecraft', text: 'Sound engine started' },
  { id: 8, time: '22:38:14.880', level: 'WARN', source: 'ResourcePack', text: '资源包 bmc2-tweaks 缺少 pack.mcmeta，已跳过' },
  { id: 9, time: '22:38:20.551', level: 'ERROR', source: 'mixin', text: 'Mixin apply failed: bettercombat.mixins.json:PlayerAttackMixin' },
  { id: 10, time: '22:38:20.553', level: 'ERROR', source: 'mixin', text: '  Class: net.minecraft.class_1657 -> net.bettercombat.mixin.PlayerAttackMixin' },
  { id: 11, time: '22:38:20.559', level: 'ERROR', source: 'mixin', text: '  Caused by: java.lang.NoSuchMethodError: method_6047()V' },
  { id: 12, time: '22:38:21.006', level: 'INFO', source: 'mixin', text: '崩溃前最后写入：crash-2026-10-26_22.41.07-client.txt' },
]