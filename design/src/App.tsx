import { useState } from 'react'
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
import Placeholder from './pages/Placeholder'
import { Shell, Sidebar, type RailKey } from './shell'
import { Button, Card, cx, Dot, Tag } from './ui'
import { backups, instances, type LaunchState, type SyncState } from './mock'

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
  const [rail, setRail] = useState<RailKey>('home')
  const [collapsed, setCollapsed] = useState(false)
  const [launch, setLaunch] = useState<LaunchState>('idle')
  const [sync, setSync] = useState<SyncState>('synced')
  const [instanceId, setInstanceId] = useState(instances[0].id)

  const current = instances.find((i) => i.id === instanceId) ?? instances[0]

  const sidebar = (
    <Sidebar
      collapsed={collapsed}
      onToggle={() => setCollapsed((v) => !v)}
      title={titles[rail]}
      footer={
        <div className="flex items-center gap-2">
          <span className="grid h-7 w-7 place-items-center rounded-sm bg-white/[0.05] font-pixel text-[9px] text-tx-2">cg</span>
          <div className="min-w-0">
            <div className="truncate text-[11.5px] text-tx-1">cg_orap</div>
            <div className="flex items-center gap-1 text-[10px] text-tx-3"><Dot /> 微软正版</div>
          </div>
        </div>
      }
    >
      {rail === 'home' || rail === 'instances' ? (
        <div className="space-y-1">
          <div className="mb-2 flex items-center gap-1.5 rounded-sm border border-border bg-black/20 px-2 py-1.5">
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
                item.id === current.id ? 'bg-white/[0.07]' : 'hover:bg-white/[0.04]',
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
          <button type="button" className="mt-1 flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-[11.5px] text-tx-3 hover:bg-white/[0.04] hover:text-tx-1">
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
            <div key={name} className="flex items-center justify-between rounded-sm px-2 py-1.5 text-[11.5px] text-tx-2 hover:bg-white/[0.04]">
              <span className="font-mono">{name}</span>
              <span className="text-[10px] text-tx-3">{hint}</span>
            </div>
          ))}
        </div>
      ) : null}

      {rail === 'backup' ? (
        <div className="space-y-1">
          {backups.map((b) => (
            <div key={b.id} className="rounded-sm px-2 py-2 hover:bg-white/[0.04]">
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
        <div className="space-y-2">
          <div className="flex items-center gap-1 rounded-sm border border-border bg-black/20 px-2 py-1.5">
            <Search className="h-3 w-3 text-tx-3" />
            <input placeholder="搜索整合包 / 模组…" className="w-full bg-transparent text-[11.5px] text-tx-1 outline-none placeholder:text-tx-3" />
          </div>
          <div className="flex items-center justify-between rounded-sm border border-border px-2 py-1.5 text-[11.5px] text-tx-2">
            来源：Modrinth
            <ChevronDown className="h-3 w-3 text-tx-3" />
          </div>
          <div className="pt-1 text-[10.5px] text-tx-3">类型</div>
          {['整合包', '模组', '光影', '材质包'].map((t) => (
            <div key={t} className="rounded-sm px-2 py-1.5 text-[11.5px] text-tx-2 hover:bg-white/[0.04]">{t}</div>
          ))}
        </div>
      ) : null}

      {rail === 'mods' || rail === 'settings' ? (
        <div className="space-y-1 text-[11.5px] text-tx-3">
          <div className="rounded-sm px-2 py-1.5 text-tx-2">占位分组</div>
          <div className="px-2 text-[10.5px] leading-relaxed">该页面为可点击占位，本期重点实现主页 / 实例详情 / 日志。</div>
        </div>
      ) : null}
    </Sidebar>
  )

  return (
    <Shell rail={rail} onRail={setRail} sidebar={sidebar} instanceName={current.name}>
      {rail === 'home' ? <Home launch={launch} setLaunch={setLaunch} sync={sync} setSync={setSync} /> : null}
      {rail === 'instances' ? <Instance /> : null}
      {rail === 'logs' ? <Logs /> : null}
      {rail === 'download' ? (
        <Placeholder
          title="资源市场"
          desc="搜索 + 来源切换（Modrinth / CurseForge）+ 类型筛选 + 右侧滑出详情抽屉。设计见文档中的线框与组件树。"
          icon={<Download className="h-4 w-4 text-tx-2" />}
          actions={['一键安装', '打开详情抽屉', '按加载器筛选']}
        />
      ) : null}
      {rail === 'mods' ? (
        <Placeholder
          title="模组管理"
          desc="按实例聚合模组列表、启用/禁用、检查更新、从市场补齐。本期为占位。"
          icon={<Package className="h-4 w-4 text-tx-2" />}
          actions={['检查更新', '批量启用']}
        />
      ) : null}
      {rail === 'backup' ? (
        <Placeholder
          title="备份与云同步"
          desc="备份时间线、云同步状态条（已同步 / 同步中 / 冲突）、恢复与清理策略。本期为占位。"
          icon={<FolderTree className="h-4 w-4 text-tx-2" />}
          actions={['立即备份', '模拟同步冲突']}
        />
      ) : null}
      {rail === 'settings' ? (
        <Placeholder
          title="设置"
          desc="分组：外观、Java、下载源、备份、联机、高级。外观提供 3 套预设主题与标题栏材质切换。"
          icon={<Settings className="h-4 w-4 text-tx-2" />}
          actions={['主题 A · 深岩', '主题 B · 苔原', '主题 C · 琥珀']}
        />
      ) : null}

      {/* 演示用状态切换器（不属于设计稿，仅方便评审） */}
      <Card elevated className="fixed bottom-4 right-4 z-40 flex items-center gap-2 px-3 py-2">
        <span className="text-[10px] text-tx-3">演示状态</span>
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