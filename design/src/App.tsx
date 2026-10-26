import { useEffect, useState } from 'react'
import {
  Download,
  FolderTree,
  Package,
  Settings,
  Boxes,
  Search,
  Plus,
  ChevronDown,
} from 'lucide-react'
import Home from './pages/Home'
import Instance from './pages/Instance'
import Logs from './pages/Logs'
import DownloadPage from './pages/Download'
import SettingsPage from './pages/Settings'
import ModsPage from './pages/Mods'
import BackupPage from './pages/Backup'
import Placeholder from './pages/Placeholder'
import { Shell, Sidebar, type RailKey } from './shell'
import { Button, Card, cx, Dot, Tag } from './ui'
import { backups, instances, type LaunchState, type SyncState } from './mock'
import { applyTheme, type ThemeId } from './themes'

const titles: Record<RailKey, string> = {
  home: '概览',
  instances: '实例',
  download: '资源市场',
  mods: '模组',
  backup: '备份与快照',
  logs: '日志',
  settings: '设置',
}

export default function App() {
  const initialRail = (() => {
    const q = new URLSearchParams(window.location.search).get('page')
    const allowed: RailKey[] = ['home', 'instances', 'download', 'mods', 'backup', 'logs', 'settings']
    return (allowed as string[]).includes(q ?? '') ? (q as RailKey) : 'home'
  })()
  const [rail, setRail] = useState<RailKey>(initialRail)
  const [collapsed, setCollapsed] = useState(() => new URLSearchParams(window.location.search).get('sidebar') === '0')

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'b') {
        e.preventDefault()
        setCollapsed((v) => !v)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])
  const [launch, setLaunch] = useState<LaunchState>('idle')
  const [sync, setSync] = useState<SyncState>('synced')
  const [instanceId, setInstanceId] = useState(instances[0].id)
  const [theme, setTheme] = useState<ThemeId>(() => {
    const q = new URLSearchParams(window.location.search).get('theme')
    if (q === 'frost' || q === 'paper' || q === 'rock') return q
    return (localStorage.getItem('orap-theme') as ThemeId | null) ?? 'rock'
  })

  useEffect(() => {
    applyTheme(theme)
    localStorage.setItem('orap-theme', theme)
  }, [theme])

  const current = instances.find((i) => i.id === instanceId) ?? instances[0]

  const sidebar = (
    <Sidebar
      collapsed={collapsed}
      onToggle={() => setCollapsed((v) => !v)}
      title={titles[rail]}
      footer={
        <div className="space-y-1.5 text-[10.5px] text-tx-3">
          <div className="flex items-center justify-between">
            <span>备份占用</span>
            <span className="font-mono text-tx-2">1.1 GB / 8 GB</span>
          </div>
          <div className="h-1 w-full overflow-hidden rounded-full bg-tint2">
            <div className="h-full w-[14%] rounded-full bg-primary/70" />
          </div>
          <div className="flex items-center gap-1.5 pt-0.5">
            <Dot tone="success" /> 上次备份 4 分钟前
          </div>
        </div>
      }
    >
      {rail === 'home' || rail === 'instances' ? (
        <div className="space-y-1">
          <div className="mb-2 flex items-center gap-1.5 rounded-sm border border-border bg-code px-2 py-1.5">
            <Search className="h-3 w-3 text-tx-3" />
            <input
              placeholder="搜索实例…"
              className="w-full bg-transparent text-[11.5px] text-tx-1 outline-none placeholder:text-tx-3"
            />
          </div>
          {instances.map((item) => (
            <button
              key={item.id}
              type="button"
              onClick={() => {
                setInstanceId(item.id)
                setRail('instances')
              }}
              className={cx(
                'flex w-full items-start gap-2.5 rounded-sm px-2 py-2 text-left transition-colors duration-150',
                item.id === current.id ? 'bg-tint2' : 'hover:bg-tint',
              )}
            >
              <span className={cx('mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-sm bg-gradient-to-br font-pixel text-[8px] text-tx-2', item.cover)}>
                {item.loader.slice(0, 2).toUpperCase()}
              </span>
              <span className="min-w-0 flex-1">
                <span className="block truncate text-[11.5px] text-tx-1">{item.name}</span>
                <span className="mt-0.5 flex items-center gap-1.5 font-mono text-[10px] text-tx-3">
                  MC {item.mc} · {item.mods} mods
                </span>
              </span>
              {item.id === current.id ? <Dot /> : null}
            </button>
          ))}
          <button type="button" className="mt-1 flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-[11.5px] text-tx-3 hover:bg-tint hover:text-tx-1">
            <Plus className="h-3.5 w-3.5" /> 新建实例
          </button>
        </div>
      ) : null}

      {rail === 'logs' ? (
        <div className="space-y-1">
          {[
            ['latest.log', '本次会话'],
            ['debug.log', '详细调试'],
            ['crash-reports', '3 份报告'],
          ].map(([name, hint]) => (
            <div key={name} className="flex items-center justify-between rounded-sm px-2 py-1.5 text-[11.5px] text-tx-2 hover:bg-tint">
              <span className="font-mono">{name}</span>
              <span className="text-[10px] text-tx-3">{hint}</span>
            </div>
          ))}
        </div>
      ) : null}

      {rail === 'backup' ? (
        <div className="space-y-1">
          {backups.map((b) => (
            <div key={b.id} className="rounded-sm px-2 py-2 hover:bg-tint">
              <div className="text-[11.5px] text-tx-1">{b.instance}</div>
              <div className="mt-0.5 flex items-center justify-between font-mono text-[10px] text-tx-3">
                <span>{b.time}</span>
                <span>{b.size}</span>
              </div>
            </div>
          ))}
        </div>
      ) : null}

      {rail === 'download' ? (
        <div className="space-y-3">
          <div>
            <div className="mb-1.5 text-[10.5px] text-tx-3">快捷</div>
            {[
              ['可更新（2）', 'Sodium · FerriteCore'],
              ['已安装（333）', '当前实例模组'],
              ['收藏（6）', '常用整合包与光影'],
            ].map(([name, hint]) => (
              <div key={name} className="rounded-sm px-2 py-1.5 hover:bg-tint">
                <div className="text-[11.5px] text-tx-2">{name}</div>
                <div className="mt-0.5 text-[10px] text-tx-3">{hint}</div>
              </div>
            ))}
          </div>
          <div className="border-t border-border pt-3">
            <div className="mb-1.5 text-[10.5px] text-tx-3">最近搜索</div>
            {['机械动力', 'shader', 'optimization'].map((q) => (
              <div key={q} className="rounded-sm px-2 py-1 text-[11.5px] text-tx-3 hover:bg-tint hover:text-tx-2">{q}</div>
            ))}
          </div>
        </div>
      ) : null}

      {rail === 'settings' ? (
        <div className="space-y-2 text-[11.5px]">
          <div className="text-[10.5px] text-tx-3">当前配置</div>
          {[
            ['主题', theme === 'rock' ? '深岩' : theme === 'frost' ? '霜白' : '暖纸'],
            ['默认内存', '4096 MB'],
            ['下载源', 'BMCLAPI 镜像'],
            ['备份', '启动前 · 保留 7 份'],
            ['数据目录', 'D:\\OrapCraft'],
          ].map(([k, v]) => (
            <div key={k} className="flex items-center justify-between rounded-sm px-2 py-1">
              <span className="text-tx-3">{k}</span>
              <span className="font-mono text-[10.5px] text-tx-2">{v}</span>
            </div>
          ))}
          <div className="mt-2 border-t border-border pt-2 text-[10px] leading-relaxed text-tx-3">
            所有配置保存到 settings.json，重启后仍然有效。
          </div>
        </div>
      ) : null}

      {rail === 'mods' ? (
        <div className="space-y-2 text-[11.5px]">
          <div className="text-[10.5px] text-tx-3">按分类</div>
          {[['优化', 4], ['内容', 2], ['库', 2], ['兼容', 1]].map(([name, count]) => (
            <div key={String(name)} className="flex items-center justify-between rounded-sm px-2 py-1.5 hover:bg-tint">
              <span className="text-tx-2">{name}</span>
              <span className="font-mono text-[10px] text-tx-3">{count}</span>
            </div>
          ))}
          <div className="mt-2 border-t border-border pt-2">
            <div className="mb-1.5 text-[10.5px] text-tx-3">可更新</div>
            {['sodium 0.6.0', 'bettercombat 1.9.0'].map((m) => (
              <div key={m} className="rounded-sm px-2 py-1 text-[10.5px] text-warning/90">{m}</div>
            ))}
          </div>
        </div>
      ) : null}
    </Sidebar>
  )

  return (
    <Shell rail={rail} onRail={setRail} sidebar={sidebar} instanceName={current.name} sidebarCollapsed={collapsed} onToggleSidebar={() => setCollapsed((v) => !v)}>
      {rail === 'home' ? (<Home launch={launch} setLaunch={setLaunch} sync={sync} setSync={setSync} onOpenLogs={() => setRail('logs')} />) : null}
      {rail === 'instances' ? <Instance /> : null}
      {rail === 'logs' ? <Logs /> : null}
      {rail === 'download' ? <DownloadPage /> : null}
      {rail === 'mods' ? <ModsPage /> : null}
      {rail === 'backup' ? <BackupPage /> : null}
      {rail === 'settings' ? <SettingsPage theme={theme} onTheme={setTheme} /> : null}

      {/* 演示用状态切换器（不属于设计稿，仅方便评审） */}
      <Card elevated className="fixed bottom-3 right-3 z-40 flex items-center gap-1.5 border-bstrong bg-elevated/80 px-2.5 py-1.5 backdrop-blur-md">
        <span className="text-[10px] text-tx-3">演示</span>
        {(['idle', 'starting', 'running'] as LaunchState[]).map((s) => (
          <button
            key={s}
            type="button"
            onClick={() => setLaunch(s)}
            className={cx(
              'rounded-sm px-1.5 py-0.5 text-[10px] transition-colors',
              launch === s ? 'bg-primary/20 text-primary' : 'text-tx-3 hover:text-tx-1',
            )}
          >
            {s === 'idle' ? '未启动' : s === 'starting' ? '启动中' : '运行中'}
          </button>
        ))}
        <span className="mx-1 h-3 w-px bg-border" />
        {(['synced', 'syncing', 'conflict'] as SyncState[]).map((s) => (
          <button
            key={s}
            type="button"
            onClick={() => setSync(s)}
            className={cx(
              'rounded-sm px-1.5 py-0.5 text-[10px] transition-colors',
              sync === s ? 'bg-warning/20 text-warning' : 'text-tx-3 hover:text-tx-1',
            )}
          >
            {s === 'synced' ? '已同步' : s === 'syncing' ? '同步中' : '冲突'}
          </button>
        ))}
      </Card>
    </Shell>
  )
}