import type { ReactNode } from 'react'
import {
  Bell,
  Boxes,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Download,
  FolderTree,
  Gamepad2,
  Home,
  Maximize2,
  Minus,
  Package,
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

export function TitleBar({ instanceName }: { instanceName: string }) {
  return (
    <header className="flex h-10 shrink-0 items-center justify-between border-b border-border bg-surface/60 px-3 backdrop-blur-md">
      <div className="flex items-center gap-2.5">
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
                : 'text-tx-2 hover:bg-tint2 hover:text-tx-1',
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
          className="grid h-9 w-9 place-items-center rounded-md text-tx-3 transition-colors hover:bg-tint2 hover:text-tx-1"
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
  if (collapsed) {
    return (
      <aside className="flex w-11 shrink-0 flex-col items-center border-r border-border bg-surface/25 pt-6">
        <button
          type="button"
          onClick={onToggle}
          className="grid h-8 w-8 shrink-0 place-items-center rounded-md border border-bstrong bg-tint text-tx-2 transition-colors duration-150 hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
          aria-label="展开侧栏"
          aria-expanded="false"
          title="展开侧栏（Ctrl+B）"
        >
          <ChevronRight className="h-3.5 w-3.5" />
        </button>
      </aside>
    )
  }

  return (
    <aside
      className={cx(
        'flex shrink-0 flex-col border-r border-border bg-surface/25 transition-[width] duration-250 ease-smooth',
        'w-[280px]',
      )}
    >
      <div className="flex h-11 items-center gap-1.5 px-2.5">
        <button
          type="button"
          onClick={onToggle}
          className="grid h-8 w-8 shrink-0 place-items-center rounded-md border border-bstrong bg-tint text-tx-2 transition-colors duration-150 hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
          aria-label="收起侧栏"
          title="收起侧栏（Ctrl+B）"
        >
          <ChevronLeft className="h-3.5 w-3.5" />
        </button>
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
      <TitleBar instanceName={instanceName} />
      <div className="flex min-h-0 flex-1">
        <Rail active={rail} onSelect={onRail} />

        {sidebar}
        <main className="relative min-w-0 flex-1 overflow-y-auto">
          <div className="mx-auto w-full max-w-[1200px] px-7 pb-4 pt-6">{children}</div>
        </main>
      </div>
    </div>
  )
}
/** 侧栏把手：贴在一级导航与侧栏之间，整条可点，收起后仍留在原位。 */
export function SidebarHandle({ collapsed, onToggle }: { collapsed: boolean; onToggle: () => void }) {
  return (
    <button
      type="button"
      onClick={onToggle}
      aria-label={collapsed ? '展开侧栏' : '收起侧栏'}
      aria-expanded={!collapsed}
      title={collapsed ? '展开侧栏（Ctrl+B）' : '收起侧栏（Ctrl+B）'}
      className="group relative flex w-4 shrink-0 items-center justify-center border-r border-border bg-tint transition-colors duration-150 hover:bg-tint2"
    >
      <span className="pointer-events-none absolute inset-y-0 left-1/2 w-[2px] -translate-x-1/2 bg-border transition-colors duration-150 group-hover:bg-primary/60" />
      <span className="relative grid h-10 w-[22px] place-items-center rounded-md border border-bstrong bg-elevated text-tx-1 shadow-soft transition-all duration-150 group-hover:scale-[1.06] group-hover:border-primary/50 group-hover:text-primary">
        {collapsed ? <ChevronRight className="h-3 w-3" /> : <ChevronLeft className="h-3.5 w-3.5" />}
      </span>
    </button>
  )
}
