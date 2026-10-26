import type { ReactNode } from 'react'
import {
  Bell,
  Boxes,
  ChevronDown,
  Download,
  FolderTree,
  Gamepad2,
  Home,
  Maximize2,
  Minus,
  Package,
  PanelLeftClose,
  PanelLeftOpen,
  ScrollText,
  Settings,
  X,
} from 'lucide-react'
import { cx } from './ui'

export type RailKey = 'home' | 'instances' | 'download' | 'mods' | 'backup' | 'logs' | 'settings'

const railItems: Array<{ key: RailKey; label: string; icon: ReactNode }> = [
  { key: 'home', label: '主页', icon: <Home className="h-[18px] w-[18px]" /> },
  { key: 'instances', label: '实例', icon: <Boxes className="h-[18px] w-[18px]" /> },
  { key: 'download', label: '下载', icon: <Download className="h-[18px] w-[18px]" /> },
  { key: 'mods', label: '模组', icon: <Package className="h-[18px] w-[18px]" /> },
  { key: 'backup', label: '备份', icon: <FolderTree className="h-[18px] w-[18px]" /> },
  { key: 'logs', label: '日志', icon: <ScrollText className="h-[18px] w-[18px]" /> },
  { key: 'settings', label: '设置', icon: <Settings className="h-[18px] w-[18px]" /> },
]

export function TitleBar({
  instanceName,
  sidebarCollapsed,
  onToggleSidebar,
}: {
  instanceName: string
  sidebarCollapsed: boolean
  onToggleSidebar: () => void
}) {
  return (
    <header className="flex h-10 shrink-0 items-center justify-between border-b border-border bg-surface/60 px-3 backdrop-blur-md">
      <div className="flex items-center gap-2.5">
        {/* 侧栏开关常驻标题栏：二级侧栏收起后这里仍可重新打开 */}
        <button
          type="button"
          onClick={onToggleSidebar}
          className="grid h-7 w-7 place-items-center rounded-sm text-tx-3 transition-colors duration-150 hover:bg-white/[0.07] hover:text-tx-1"
          aria-label={sidebarCollapsed ? '展开侧栏' : '收起侧栏'}
          aria-expanded={!sidebarCollapsed}
          title={sidebarCollapsed ? '展开侧栏 (Ctrl+B)' : '收起侧栏 (Ctrl+B)'}
        >
          {sidebarCollapsed ? <PanelLeftOpen className="h-3.5 w-3.5" /> : <PanelLeftClose className="h-3.5 w-3.5" />}
        </button>
        <div className="grid h-6 w-6 place-items-center rounded-sm bg-primary/15">
          <span className="font-pixel text-[11px] leading-none text-primary">O</span>
        </div>
        <span className="text-[12.5px] font-medium tracking-wide text-tx-1">Orap Craft</span>
        <span className="text-[10px] text-tx-3">v0.2 UI 预览</span>
      </div>

      <div className="flex items-center gap-2 text-[11.5px] text-tx-2">
        <Gamepad2 className="h-3.5 w-3.5 text-tx-3" />
        <span className="font-mono text-tx-1">{instanceName}</span>
      </div>

      <div className="flex items-center gap-1">
        {[Minus, Maximize2, X].map((Icon, i) => (
          <button
            key={i}
            type="button"
            className={cx(
              'grid h-7 w-8 place-items-center rounded-sm text-tx-2 transition-colors duration-150 ease-smooth',
              i === 2 ? 'hover:bg-danger/80 hover:text-white' : 'hover:bg-white/10 hover:text-tx-1',
            )}
            aria-label={['最小化', '最大化', '关闭'][i]}
          >
            <Icon className="h-3.5 w-3.5" />
          </button>
        ))}
      </div>
    </header>
  )
}

export function Rail({
  active,
  onSelect,
}: {
  active: RailKey
  onSelect: (key: RailKey) => void
}) {
  return (
    <nav className="flex w-[72px] shrink-0 flex-col items-center gap-1 border-r border-border bg-surface/40 py-3">
      {railItems.map((item) => {
        const isActive = item.key === active
        return (
          <button
            key={item.key}
            type="button"
            onClick={() => onSelect(item.key)}
            className={cx(
              'group relative grid h-11 w-11 place-items-center rounded-md transition-all duration-200 ease-smooth',
              isActive
                ? 'bg-primary/12 text-primary'
                : 'text-tx-2 hover:bg-white/[0.06] hover:text-tx-1',
            )}
            aria-current={isActive ? 'page' : undefined}
          >
            {isActive ? (
              <span className="absolute left-0 h-5 w-[2px] rounded-full bg-primary" />
            ) : null}
            {item.icon}
            <span className="pointer-events-none absolute left-[52px] z-20 whitespace-nowrap rounded-sm border border-border bg-elevated px-2 py-1 text-[11px] text-tx-1 opacity-0 shadow-soft transition-opacity duration-150 group-hover:opacity-100">
              {item.label}
            </span>
          </button>
        )
      })}

      <div className="mt-auto flex flex-col items-center gap-2">
        <button
          type="button"
          className="grid h-9 w-9 place-items-center rounded-md text-tx-3 transition-colors hover:bg-white/[0.06] hover:text-tx-1"
          aria-label="通知"
        >
          <Bell className="h-4 w-4" />
        </button>
        <button
          type="button"
          className="relative h-9 w-9 overflow-hidden rounded-md border border-border bg-hover"
          aria-label="账户"
        >
          <span className="grid h-full w-full place-items-center text-[11px] font-medium text-tx-2">cg</span>
          <span className="absolute bottom-0.5 right-0.5 h-2 w-2 rounded-full border border-surface bg-success" />
        </button>
      </div>
    </nav>
  )
}

export function Sidebar({
  collapsed,
  onToggle,
  title,
  children,
  footer,
}: {
  collapsed: boolean
  onToggle: () => void
  title: string
  children: ReactNode
  footer?: ReactNode
}) {
  return (
    <aside
      className={cx(
        'flex shrink-0 flex-col border-r border-border bg-surface/25 transition-[width] duration-250 ease-smooth',
        collapsed ? 'w-0 overflow-hidden' : 'w-[280px]',
      )}
    >
      <div className="flex h-11 items-center px-4">
        <span className="text-[12px] font-medium tracking-wide text-tx-2">{title}</span>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto px-3 pb-3">{children}</div>
      {footer ? <div className="border-t border-border px-3 py-3">{footer}</div> : null}
    </aside>
  )
}

export function Shell({
  rail,
  onRail,
  sidebar,
  children,
  instanceName,
  sidebarCollapsed,
  onToggleSidebar,
}: {
  rail: RailKey
  onRail: (key: RailKey) => void
  sidebar: ReactNode
  children: ReactNode
  instanceName: string
  sidebarCollapsed: boolean
  onToggleSidebar: () => void
}) {
  return (
    <div className="flex h-full flex-col bg-bg">
      <TitleBar instanceName={instanceName} sidebarCollapsed={sidebarCollapsed} onToggleSidebar={onToggleSidebar} />
      <div className="flex min-h-0 flex-1">
        <Rail active={rail} onSelect={onRail} />
        {sidebar}
        <main className="min-w-0 flex-1 overflow-y-auto">
          <div className="mx-auto w-full max-w-[1200px] px-7 pb-4 pt-6">{children}</div>
        </main>
      </div>
    </div>
  )
}
