import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import {
  ArrowLeft,
  Check,
  Download,
  FilePlus2,
  Hash,
  Loader2,
  MessageCircle,
  MoreHorizontal,
  Paperclip,
  Pencil,
  Pin,
  Plus,
  Reply,
  Search,
  Send,
  Trash2,
  UserRound,
  Users,
  X,
} from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useToast } from '../../hooks/useToast';
import {
  internalChatApi,
  type InternalChatMessage,
  type InternalChatNotificationMode,
  type InternalChatPerson,
  type InternalChatThread,
} from '../../services/internalChatApi';
import {
  createNotificationsHubConnection,
  REALTIME_TASK_JOURNAL_CHANGED_EVENT,
  stopNotificationsHubConnection,
} from '../../services/realtimeNotifications';
import { localChatDayKey } from '../../utils/chatDateTime';

const REACTIONS = ['👍', '❤️', '👏', '✅', '👀'];

function formatMessageTime(value: string) {
  return new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit' }).format(new Date(value));
}

function formatDay(value: string) {
  return new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' }).format(new Date(value));
}

function formatFileSize(value: number) {
  if (value < 1024) return `${value} Б`;
  if (value < 1024 * 1024) return `${Math.round(value / 1024)} КБ`;
  return `${(value / 1024 / 1024).toFixed(1)} МБ`;
}

function renderFormattedText(text: string, peopleById: Map<number, InternalChatPerson>): ReactNode {
  const mentions = text.split(/(@\[\d+\]|@\d+\b)/g);
  return mentions.map((part, partIndex) => {
    const mention = /^@(?:\[(\d+)\]|(\d+))$/.exec(part);
    if (mention) {
      const person = peopleById.get(Number(mention[1] || mention[2]));
      return person
        ? <span key={partIndex} className="rounded bg-blue-100 px-1 font-semibold text-blue-800" title={person.email}>@{person.name}</span>
        : <span key={partIndex} className="rounded bg-slate-100 px-1 text-slate-600">@участник #{mention[1] || mention[2]}</span>;
    }
    return <span key={partIndex}>{renderPlainFormattedText(part)}</span>;
  });
}

function renderPlainFormattedText(text: string): ReactNode {
  const tokens = text.split(/(https?:\/\/[^\s]+|\*\*[^*]+\*\*|`[^`]+`|\*[^*]+\*)/g);
  return tokens.map((token, index) => {
    if (/^https?:\/\//.test(token)) return <a key={index} href={token} target="_blank" rel="noreferrer" className="underline">{token}</a>;
    if (token.startsWith('**') && token.endsWith('**')) return <strong key={index}>{token.slice(2, -2)}</strong>;
    if (token.startsWith('`') && token.endsWith('`')) return <code key={index} className="rounded bg-slate-900/10 px-1 py-0.5">{token.slice(1, -1)}</code>;
    if (token.startsWith('*') && token.endsWith('*')) return <em key={index}>{token.slice(1, -1)}</em>;
    return token;
  });
}

function threadIcon(kind: InternalChatThread['kind']) {
  return kind === 'direct' ? UserRound : kind === 'task' ? Check : Hash;
}

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : 'Не удалось выполнить действие.';
}

function SecureImageAttachment({ attachment, onError }: { attachment: InternalChatMessage['attachments'][number]; onError: (error: unknown) => void }) {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    let active = true;
    let objectUrl: string | null = null;
    void internalChatApi.getAttachmentBlob(attachment.id)
      .then((blob) => {
        if (!active) return;
        objectUrl = URL.createObjectURL(blob);
        setUrl(objectUrl);
      })
      .catch(onError);
    return () => { active = false; if (objectUrl) URL.revokeObjectURL(objectUrl); };
  }, [attachment.id, onError]);
  if (!url) return <div className="flex h-28 items-center justify-center rounded-lg bg-slate-100 text-xs text-slate-500"><Loader2 className="mr-2 h-4 w-4 animate-spin" />Загрузка изображения</div>;
  return <button type="button" onClick={() => void internalChatApi.downloadAttachment(attachment).catch(onError)} className="block w-full overflow-hidden rounded-lg"><img src={url} alt={attachment.fileName} className="max-h-72 w-full object-contain" /></button>;
}

interface ConversationProps {
  thread: InternalChatThread;
  people: InternalChatPerson[];
  currentUserId: number;
  compact?: boolean;
  onBack?: () => void;
  onThreadChanged?: () => void;
  onOpenTask?: (taskId: number) => void;
}

function ChatConversation({ thread, people, currentUserId, compact = false, onBack, onThreadChanged, onOpenTask }: ConversationProps) {
  const { user, isAdmin } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const canCreateTasks = isAdmin() || permissions.tasks.canCreate;
  const toast = useToast();
  const showError = useCallback((error: unknown) => toast.error(errorMessage(error)), [toast]);
  const [messages, setMessages] = useState<InternalChatMessage[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadingEarlier, setLoadingEarlier] = useState(false);
  const [hasMore, setHasMore] = useState(false);
  const [text, setText] = useState('');
  const [files, setFiles] = useState<File[]>([]);
  const [sending, setSending] = useState(false);
  const [replyTo, setReplyTo] = useState<InternalChatMessage | null>(null);
  const [editing, setEditing] = useState<InternalChatMessage | null>(null);
  const [actionsFor, setActionsFor] = useState<number | null>(null);
  const [searchOpen, setSearchOpen] = useState(false);
  const [search, setSearch] = useState('');
  const [appliedSearch, setAppliedSearch] = useState('');
  const [authorUserId, setAuthorUserId] = useState('');
  const [dateFrom, setDateFrom] = useState('');
  const [dateTo, setDateTo] = useState('');
  const [mentionedMe, setMentionedMe] = useState(false);
  const [taskSource, setTaskSource] = useState<InternalChatMessage | null>(null);
  const [taskTitle, setTaskTitle] = useState('');
  const [taskDueAt, setTaskDueAt] = useState('');
  const [taskAssigneeId, setTaskAssigneeId] = useState('');
  const [taskSaving, setTaskSaving] = useState(false);
  const [membersOpen, setMembersOpen] = useState(false);
  const [memberUserIds, setMemberUserIds] = useState<number[]>(thread.memberUserIds ?? []);
  const [membersSaving, setMembersSaving] = useState(false);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  const bottomRef = useRef<HTMLDivElement | null>(null);
  const loadRequestId = useRef(0);
  const threadMemberIdsKey = (thread.memberUserIds ?? []).join(',');
  const peopleById = useMemo(() => new Map(people.map((person) => [person.id, person])), [people]);
  const otherMemberNames = (thread.memberUserIds ?? [])
    .filter((id) => id !== currentUserId)
    .map((id) => peopleById.get(id)?.name || `Участник #${id}`);
  const memberSummary = otherMemberNames.length > 3
    ? `${otherMemberNames.slice(0, 3).join(', ')} и ещё ${otherMemberNames.length - 3}`
    : otherMemberNames.join(', ');

  const loadMessages = useCallback(async (beforeId?: number, appendEarlier = false) => {
    const requestId = ++loadRequestId.current;
    if (appendEarlier) setLoadingEarlier(true);
    else setLoading(true);
    try {
      const page = await internalChatApi.getMessages(thread.id, {
        beforeId,
        search: appliedSearch || undefined,
        authorUserId: authorUserId ? Number(authorUserId) : undefined,
        from: dateFrom ? new Date(`${dateFrom}T00:00:00`).toISOString() : undefined,
        to: dateTo ? new Date(`${dateTo}T23:59:59.999`).toISOString() : undefined,
        mentionedMe,
      });
      if (requestId !== loadRequestId.current) return;
      setMessages((current) => appendEarlier ? [...page.items, ...current] : page.items);
      setHasMore(page.hasMore);
      const isFiltered = Boolean(appliedSearch || authorUserId || dateFrom || dateTo || mentionedMe);
      if (!appendEarlier && page.items.length && !isFiltered) {
        await internalChatApi.markRead(thread.id, page.items.at(-1)?.id);
        onThreadChanged?.();
        window.setTimeout(() => bottomRef.current?.scrollIntoView?.({ block: 'end' }), 0);
      }
    } catch (error) {
      if (requestId === loadRequestId.current) toast.error(errorMessage(error));
    } finally {
      if (requestId === loadRequestId.current) {
        setLoading(false);
        setLoadingEarlier(false);
      }
    }
  }, [appliedSearch, authorUserId, dateFrom, dateTo, mentionedMe, onThreadChanged, thread.id, toast]);

  useEffect(() => {
    setMessages([]);
    setText('');
    setFiles([]);
    setReplyTo(null);
    setEditing(null);
    setMemberUserIds(threadMemberIdsKey ? threadMemberIdsKey.split(',').map(Number) : []);
    void loadMessages();
  }, [loadMessages, thread.id, threadMemberIdsKey]);

  useEffect(() => {
    const connection = createNotificationsHubConnection();
    connection.on(REALTIME_TASK_JOURNAL_CHANGED_EVENT, () => void loadMessages());
    void connection.start().catch(() => undefined);
    return () => { void stopNotificationsHubConnection(connection); };
  }, [loadMessages]);

  const groupedMessages = useMemo(() => {
    const groups: Array<{ day: string; items: InternalChatMessage[] }> = [];
    messages.forEach((message) => {
      const day = localChatDayKey(message.createdAt);
      const group = groups.at(-1);
      if (!group || group.day !== day) groups.push({ day, items: [message] });
      else group.items.push(message);
    });
    return groups;
  }, [messages]);

  const insertMarkup = (left: string, right = left) => {
    const element = textareaRef.current;
    if (!element) return;
    const start = element.selectionStart;
    const end = element.selectionEnd;
    setText((value) => `${value.slice(0, start)}${left}${value.slice(start, end)}${right}${value.slice(end)}`);
    window.setTimeout(() => element.focus(), 0);
  };

  const insertMention = (person: InternalChatPerson) => {
    setText((value) => `${value}${value && !value.endsWith(' ') ? ' ' : ''}@[${person.id}] `);
    textareaRef.current?.focus();
  };

  const submit = async () => {
    if (editing) {
      if (!text.trim()) return;
      setSending(true);
      try {
        const updated = await internalChatApi.editMessage(editing.id, text);
        setMessages((current) => current.map((item) => item.id === updated.id ? updated : item));
        setEditing(null);
        setText('');
      } catch (error) { toast.error(errorMessage(error)); }
      finally { setSending(false); }
      return;
    }
    if (!text.trim() && files.length === 0) return;
    setSending(true);
    try {
      const created = await internalChatApi.sendMessage(thread.id, text, files, replyTo?.id);
      setMessages((current) => [...current, created]);
      setText('');
      setFiles([]);
      setReplyTo(null);
      onThreadChanged?.();
      window.setTimeout(() => bottomRef.current?.scrollIntoView?.({ behavior: 'smooth' }), 0);
    } catch (error) { toast.error(errorMessage(error)); }
    finally { setSending(false); }
  };

  const refreshMessage = async () => {
    await loadMessages();
    onThreadChanged?.();
  };

  const setMode = async (mode: InternalChatNotificationMode) => {
    try {
      await internalChatApi.setNotificationMode(thread.id, mode);
      thread.notificationMode = mode;
      onThreadChanged?.();
    } catch (error) { toast.error(errorMessage(error)); }
  };

  const submitTask = async () => {
    if (!taskSource || !taskTitle.trim() || !taskDueAt) return;
    setTaskSaving(true);
    try {
      const created = await internalChatApi.createTask(taskSource.id, {
        title: taskTitle.trim(),
        dueAt: new Date(taskDueAt).toISOString(),
        assigneeUserId: taskAssigneeId ? Number(taskAssigneeId) : null,
      });
      setMessages((current) => [...current, created]);
      setTaskSource(null);
      setTaskTitle('');
      setTaskDueAt('');
      setTaskAssigneeId('');
      toast.success('Задача создана и связана с сообщением.');
    } catch (error) { toast.error(errorMessage(error)); }
    finally { setTaskSaving(false); }
  };

  const saveMembers = async () => {
    setMembersSaving(true);
    try {
      await internalChatApi.updateMembers(thread.id, memberUserIds);
      setMembersOpen(false);
      onThreadChanged?.();
      toast.success('Состав канала обновлён.');
    } catch (error) { toast.error(errorMessage(error)); }
    finally { setMembersSaving(false); }
  };

  return (
    <section className={`flex min-h-0 flex-1 flex-col overflow-hidden bg-white ${compact ? 'rounded-xl border border-slate-200' : ''}`}>
      <header className="flex items-center gap-2 border-b border-slate-200 px-3 py-2.5">
        {onBack ? <button type="button" onClick={onBack} className="rounded-lg p-2 text-slate-500 hover:bg-slate-100 lg:hidden" aria-label="К списку чатов"><ArrowLeft className="h-4 w-4" /></button> : null}
        <div className="min-w-0 flex-1">
          <div className="truncate text-sm font-semibold text-slate-900">{thread.name}</div>
          <div className="truncate text-xs text-slate-500">{thread.kind === 'task' ? `Чат задачи${memberSummary ? ` · Участники: ${memberSummary}` : ''}` : thread.kind === 'direct' ? `Личный диалог${memberSummary ? ` с ${memberSummary}` : ''}` : `Командный канал${memberSummary ? ` · Участники: ${memberSummary}` : ''}`}</div>
        </div>
        <button type="button" onClick={() => {
          if (searchOpen) {
            setSearch('');
            setAppliedSearch('');
            setAuthorUserId('');
            setDateFrom('');
            setDateTo('');
            setMentionedMe(false);
          }
          setSearchOpen((value) => !value);
        }} className="rounded-lg p-2 text-slate-500 hover:bg-slate-100" aria-label="Поиск"><Search className="h-4 w-4" /></button>
        {thread.kind === 'channel' && thread.role === 'owner' ? <button type="button" onClick={() => { setMemberUserIds(thread.memberUserIds ?? []); setMembersOpen(true); }} className="rounded-lg p-2 text-slate-500 hover:bg-slate-100" aria-label="Участники канала" title="Участники канала"><Users className="h-4 w-4" /></button> : null}
        <select value={thread.notificationMode} onChange={(event) => void setMode(event.target.value as InternalChatNotificationMode)} className="h-9 rounded-lg border border-slate-200 bg-white px-2 text-xs text-slate-600" aria-label="Уведомления чата">
          <option value="all">Все уведомления</option>
          <option value="mentions">Только упоминания</option>
          <option value="muted">Отключены</option>
        </select>
      </header>

      {searchOpen ? (
        <div className="grid gap-2 border-b border-slate-200 bg-slate-50 p-3 sm:grid-cols-2 lg:grid-cols-[minmax(0,1fr)_180px_145px_145px_auto_auto]">
          <input value={search} onChange={(event) => setSearch(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter') setAppliedSearch(search.trim()); }} placeholder="Текст сообщения" className="h-9 rounded-lg border border-slate-200 bg-white px-3 text-sm" />
          <select value={authorUserId} onChange={(event) => setAuthorUserId(event.target.value)} className="h-9 rounded-lg border border-slate-200 bg-white px-2 text-sm">
            <option value="">Все авторы</option>
            {people.map((person) => <option key={person.id} value={person.id}>{person.name}</option>)}
          </select>
          <input type="date" value={dateFrom} onChange={(event) => setDateFrom(event.target.value)} className="h-9 rounded-lg border border-slate-200 bg-white px-2 text-sm" aria-label="Сообщения с даты" />
          <input type="date" value={dateTo} onChange={(event) => setDateTo(event.target.value)} className="h-9 rounded-lg border border-slate-200 bg-white px-2 text-sm" aria-label="Сообщения по дату" />
          <label className="inline-flex items-center gap-2 text-xs text-slate-600"><input type="checkbox" checked={mentionedMe} onChange={(event) => setMentionedMe(event.target.checked)} />Упомянули меня</label>
          <button type="button" onClick={() => { if (appliedSearch === search.trim()) void loadMessages(); else setAppliedSearch(search.trim()); }} className="rounded-lg bg-slate-900 px-3 py-2 text-xs font-semibold text-white">Найти</button>
        </div>
      ) : null}

      <div className={`min-h-0 flex-1 overflow-y-auto bg-slate-50/60 p-3 ${compact ? 'max-h-[420px]' : ''}`}>
        {hasMore ? <button type="button" disabled={loadingEarlier} onClick={() => void loadMessages(messages[0]?.id, true)} className="mx-auto mb-3 block rounded-full border border-slate-200 bg-white px-4 py-1.5 text-xs font-semibold text-slate-600">{loadingEarlier ? 'Загрузка...' : 'Показать более ранние'}</button> : null}
        {loading ? <div className="flex h-full items-center justify-center text-sm text-slate-500"><Loader2 className="mr-2 h-4 w-4 animate-spin" />Загружаем сообщения</div> : messages.length === 0 ? <div className="flex h-full min-h-48 items-center justify-center text-sm text-slate-500">Сообщений пока нет. Начните обсуждение.</div> : groupedMessages.map((group) => (
          <div key={group.day}>
            <div className="sticky top-0 z-10 mx-auto my-3 w-fit rounded-full bg-white/95 px-3 py-1 text-[11px] font-semibold text-slate-500 shadow-sm ring-1 ring-slate-200">{formatDay(group.items[0].createdAt)}</div>
            <div className="space-y-2">
              {group.items.map((message) => {
                const mine = message.authorUserId === currentUserId;
                const deleted = Boolean(message.deletedAt);
                return (
                  <article key={message.id} className={`group flex ${mine ? 'justify-end' : 'justify-start'}`}>
                    <div className={`relative max-w-[88%] rounded-2xl px-3 py-2 text-sm shadow-sm ${deleted ? 'border border-dashed border-slate-200 bg-white text-slate-400' : mine ? 'bg-blue-600 text-white' : message.kind === 'task-link' ? 'border border-emerald-200 bg-emerald-50 text-slate-700' : 'border border-slate-200 bg-white text-slate-700'}`}>
                      {message.isPinned ? <div className={`mb-1 flex items-center gap-1 text-[11px] font-semibold ${mine ? 'text-blue-100' : 'text-amber-700'}`}><Pin className="h-3 w-3" />Закреплено</div> : null}
                      {!mine && !deleted ? <div className="mb-1 text-xs font-semibold text-slate-900">{message.authorName}</div> : null}
                      {message.replyToMessageId ? <div className={`mb-2 rounded-lg border-l-2 px-2 py-1 text-xs ${mine ? 'border-blue-200 bg-blue-500/40 text-blue-50' : 'border-slate-300 bg-slate-50 text-slate-500'}`}><strong>{message.replyAuthorName}</strong><div className="truncate">{message.replyPreview}</div></div> : null}
                      {deleted ? <span>Сообщение удалено</span> : <div className="whitespace-pre-wrap break-words">{renderFormattedText(message.text, peopleById)}</div>}
                      {!deleted && message.attachments.length ? <div className="mt-2 space-y-1.5">{message.attachments.map((attachment) => attachment.contentType.startsWith('image/') ? <SecureImageAttachment key={attachment.id} attachment={attachment} onError={showError} /> : <button key={attachment.id} type="button" onClick={() => void internalChatApi.downloadAttachment(attachment).catch(showError)} className={`flex w-full items-center gap-2 rounded-lg px-2 py-2 text-left text-xs ${mine ? 'bg-blue-500/60 text-white' : 'bg-slate-100 text-slate-700'}`}><Paperclip className="h-4 w-4" /><span className="min-w-0 flex-1 truncate">{attachment.fileName}</span><span>{formatFileSize(attachment.size)}</span><Download className="h-3.5 w-3.5" /></button>)}</div> : null}
                      {message.createdTaskJournalItemId ? <button type="button" onClick={() => onOpenTask?.(message.createdTaskJournalItemId!)} className="mt-2 rounded-lg bg-emerald-600 px-3 py-1.5 text-xs font-semibold text-white">Открыть задачу #{message.createdTaskJournalItemId}</button> : null}
                      {!deleted && message.reactions.length ? <div className="mt-2 flex flex-wrap gap-1">{message.reactions.map((reaction) => <button key={reaction.emoji} type="button" onClick={() => void internalChatApi.toggleReaction(message.id, reaction.emoji).then(refreshMessage).catch((error) => toast.error(errorMessage(error)))} className={`rounded-full px-2 py-0.5 text-xs ring-1 ${reaction.userIds.includes(currentUserId) ? 'bg-amber-100 text-amber-900 ring-amber-300' : 'bg-white text-slate-700 ring-slate-200'}`}>{reaction.emoji} {reaction.count}</button>)}</div> : null}
                      <div className={`mt-1 flex items-center justify-end gap-1 text-[10px] ${mine && !deleted ? 'text-blue-100' : 'text-slate-400'}`}>{message.editedAt ? 'изменено · ' : ''}{formatMessageTime(message.createdAt)}{mine && (message.readByUserIds?.length ?? 0) > 0 ? ` · прочитано: ${message.readByUserIds.length}` : ''}</div>
                      {!deleted ? <button type="button" onClick={() => setActionsFor(actionsFor === message.id ? null : message.id)} className={`absolute -top-2 ${mine ? '-left-8' : '-right-8'} hidden rounded-full bg-white p-1.5 text-slate-500 shadow ring-1 ring-slate-200 group-hover:block`} aria-label="Действия"><MoreHorizontal className="h-4 w-4" /></button> : null}
                      {actionsFor === message.id ? <div className={`absolute top-7 z-20 w-52 overflow-hidden rounded-xl border border-slate-200 bg-white py-1 text-xs text-slate-700 shadow-xl ${mine ? 'right-full mr-2' : 'left-full ml-2'}`}>
                        <button type="button" onClick={() => { setReplyTo(message); setActionsFor(null); textareaRef.current?.focus(); }} className="flex w-full items-center gap-2 px-3 py-2 hover:bg-slate-50"><Reply className="h-3.5 w-3.5" />Ответить</button>
                        <div className="flex gap-1 border-y border-slate-100 px-2 py-2">{REACTIONS.map((emoji) => <button key={emoji} type="button" onClick={() => void internalChatApi.toggleReaction(message.id, emoji).then(refreshMessage)} className="rounded p-1 hover:bg-slate-100">{emoji}</button>)}</div>
                        <button type="button" onClick={() => void internalChatApi.setPinned(message.id, !message.isPinned).then(refreshMessage).catch((error) => toast.error(errorMessage(error)))} className="flex w-full items-center gap-2 px-3 py-2 hover:bg-slate-50"><Pin className="h-3.5 w-3.5" />{message.isPinned ? 'Открепить' : 'Закрепить'}</button>
                        {canCreateTasks ? <button type="button" onClick={() => { setTaskSource(message); setTaskTitle(message.text.slice(0, 120)); setActionsFor(null); }} className="flex w-full items-center gap-2 px-3 py-2 hover:bg-slate-50"><FilePlus2 className="h-3.5 w-3.5" />Создать задачу</button> : null}
                        {mine && message.kind === 'user' ? <><button type="button" onClick={() => { setEditing(message); setText(message.text); setActionsFor(null); }} className="flex w-full items-center gap-2 px-3 py-2 hover:bg-slate-50"><Pencil className="h-3.5 w-3.5" />Редактировать</button><button type="button" onClick={() => void internalChatApi.deleteMessage(message.id).then(refreshMessage).catch((error) => toast.error(errorMessage(error)))} className="flex w-full items-center gap-2 px-3 py-2 text-rose-600 hover:bg-rose-50"><Trash2 className="h-3.5 w-3.5" />Удалить</button></> : null}
                      </div> : null}
                    </div>
                  </article>
                );
              })}
            </div>
          </div>
        ))}
        <div ref={bottomRef} />
      </div>

      <footer className="border-t border-slate-200 bg-white p-3">
        {replyTo || editing ? <div className="mb-2 flex items-center gap-2 rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-600"><Reply className="h-3.5 w-3.5" /><span className="min-w-0 flex-1 truncate">{editing ? 'Редактирование сообщения' : `Ответ для ${replyTo?.authorName}: ${replyTo?.text}`}</span><button type="button" onClick={() => { setReplyTo(null); setEditing(null); setText(''); }}><X className="h-4 w-4" /></button></div> : null}
        {files.length ? <div className="mb-2 flex flex-wrap gap-1.5">{files.map((file, index) => <span key={`${file.name}-${index}`} className="inline-flex items-center gap-1 rounded-full bg-blue-50 px-2 py-1 text-xs text-blue-700"><Paperclip className="h-3 w-3" />{file.name}<button type="button" onClick={() => setFiles((current) => current.filter((_, itemIndex) => itemIndex !== index))}><X className="h-3 w-3" /></button></span>)}</div> : null}
        <div className="mb-1 flex items-center gap-1 text-xs text-slate-500">
          <button type="button" onClick={() => insertMarkup('**')} className="rounded px-2 py-1 font-bold hover:bg-slate-100" title="Жирный">B</button>
          <button type="button" onClick={() => insertMarkup('*')} className="rounded px-2 py-1 italic hover:bg-slate-100" title="Курсив">I</button>
          <button type="button" onClick={() => insertMarkup('`')} className="rounded px-2 py-1 font-mono hover:bg-slate-100" title="Код">&lt;/&gt;</button>
          <details className="relative"><summary className="list-none rounded px-2 py-1 hover:bg-slate-100">@</summary><div className="absolute bottom-8 left-0 z-30 max-h-48 w-64 overflow-y-auto rounded-xl border border-slate-200 bg-white p-1 shadow-xl">{people.map((person) => <button key={person.id} type="button" onClick={() => insertMention(person)} className="block w-full truncate rounded-lg px-2 py-2 text-left hover:bg-slate-50">{person.name}</button>)}</div></details>
        </div>
        <div className="flex items-end gap-2 rounded-xl border border-slate-200 bg-slate-50 p-2 focus-within:border-blue-300 focus-within:ring-2 focus-within:ring-blue-100">
          <input ref={fileInputRef} type="file" multiple className="hidden" onChange={(event) => setFiles(Array.from(event.target.files ?? []).slice(0, 10))} />
          <button type="button" onClick={() => fileInputRef.current?.click()} className="rounded-lg p-2 text-slate-500 hover:bg-white" title="Прикрепить файлы"><Paperclip className="h-4 w-4" /></button>
          <textarea ref={textareaRef} value={text} onChange={(event) => setText(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); void submit(); } }} rows={1} maxLength={8000} placeholder="Напишите сообщение…" className="max-h-32 min-h-9 flex-1 resize-y border-0 bg-transparent px-1 py-2 text-sm outline-none" />
          <button type="button" onClick={() => void submit()} disabled={sending || (!text.trim() && files.length === 0)} className="inline-flex h-9 w-9 items-center justify-center rounded-lg bg-blue-600 text-white disabled:bg-slate-300" aria-label="Отправить">{sending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}</button>
        </div>
      </footer>

      {taskSource ? <div className="fixed inset-0 z-[90] flex items-center justify-center bg-slate-950/40 p-4"><div className="w-full max-w-md rounded-2xl bg-white p-4 shadow-2xl"><div className="flex items-center justify-between"><h2 className="font-semibold text-slate-900">Задача из сообщения</h2><button type="button" onClick={() => setTaskSource(null)}><X className="h-5 w-5" /></button></div><label className="mt-4 block text-xs font-semibold text-slate-600">Название<input value={taskTitle} onChange={(event) => setTaskTitle(event.target.value)} maxLength={500} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /></label><label className="mt-3 block text-xs font-semibold text-slate-600">Срок<input type="datetime-local" value={taskDueAt} onChange={(event) => setTaskDueAt(event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /></label><label className="mt-3 block text-xs font-semibold text-slate-600">Исполнитель<select value={taskAssigneeId} onChange={(event) => setTaskAssigneeId(event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm"><option value="">Не назначен</option>{people.map((person) => <option key={person.id} value={person.id}>{person.name}</option>)}</select></label><button type="button" onClick={() => void submitTask()} disabled={taskSaving || !taskTitle.trim() || !taskDueAt} className="mt-4 w-full rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white disabled:bg-slate-300">{taskSaving ? 'Создаём...' : 'Создать задачу'}</button></div></div> : null}
      {membersOpen ? <div className="fixed inset-0 z-[90] flex items-center justify-center bg-slate-950/40 p-4"><div className="w-full max-w-md rounded-2xl bg-white p-4 shadow-2xl"><div className="flex items-center justify-between"><h2 className="font-semibold text-slate-900">Участники канала</h2><button type="button" onClick={() => setMembersOpen(false)}><X className="h-5 w-5" /></button></div><div className="mt-3 max-h-72 overflow-y-auto rounded-xl border border-slate-200 p-2">{people.map((person) => { const selected = memberUserIds.includes(person.id); const owner = person.id === currentUserId; return <label key={person.id} className="flex cursor-pointer items-center gap-3 rounded-lg px-2 py-2 hover:bg-slate-50"><input type="checkbox" checked={selected} disabled={owner} onChange={() => setMemberUserIds((current) => selected ? current.filter((id) => id !== person.id) : [...current, person.id])} /><span className="min-w-0"><span className="block truncate text-sm font-medium text-slate-800">{person.name}{owner ? ' · владелец' : ''}</span><span className="block truncate text-xs text-slate-500">{person.email}</span></span></label>; })}</div><button type="button" disabled={membersSaving} onClick={() => void saveMembers()} className="mt-4 w-full rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white disabled:bg-slate-300">{membersSaving ? 'Сохраняем...' : 'Сохранить участников'}</button></div></div> : null}
    </section>
  );
}

export function InternalMessenger({ onOpenTask }: { onOpenTask?: (taskId: number) => void }) {
  const { user } = useAuth();
  const toast = useToast();
  const [threads, setThreads] = useState<InternalChatThread[]>([]);
  const [people, setPeople] = useState<InternalChatPerson[]>([]);
  const [activeId, setActiveId] = useState<number | null>(() => {
    const value = new URLSearchParams(window.location.search).get('thread');
    return value ? Number(value) || null : null;
  });
  const [loading, setLoading] = useState(true);
  const [createOpen, setCreateOpen] = useState(false);
  const [createKind, setCreateKind] = useState<'channel' | 'direct'>('channel');
  const [channelName, setChannelName] = useState('');
  const [selectedPeople, setSelectedPeople] = useState<number[]>([]);
  const [creating, setCreating] = useState(false);
  const [filter, setFilter] = useState('');

  const load = useCallback(async () => {
    try {
      const [nextThreads, nextPeople] = await Promise.all([internalChatApi.getThreads(), internalChatApi.getPeople()]);
      setThreads(nextThreads);
      setPeople(nextPeople);
      setActiveId((current) => current && nextThreads.some((thread) => thread.id === current) ? current : nextThreads[0]?.id ?? null);
    } catch (error) { toast.error(errorMessage(error)); }
    finally { setLoading(false); }
  }, [toast]);

  useEffect(() => { void load(); }, [load]);
  const active = threads.find((thread) => thread.id === activeId) ?? null;
  const filtered = threads.filter((thread) => thread.name.toLowerCase().includes(filter.trim().toLowerCase()));

  const create = async () => {
    setCreating(true);
    try {
      const thread = createKind === 'channel'
        ? await internalChatApi.createChannel(channelName, selectedPeople)
        : await internalChatApi.openDirect(selectedPeople[0]);
      setCreateOpen(false);
      setChannelName('');
      setSelectedPeople([]);
      await load();
      setActiveId(thread.id);
    } catch (error) { toast.error(errorMessage(error)); }
    finally { setCreating(false); }
  };

  if (!user) return null;
  return (
    <div className="flex h-[calc(100vh-5.5rem)] min-h-[520px] overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm">
      <aside className={`${active ? 'hidden lg:flex' : 'flex'} w-full shrink-0 flex-col border-r border-slate-200 lg:w-80`}>
        <div className="flex items-center justify-between border-b border-slate-200 p-3"><div><h1 className="font-semibold text-slate-900">Мессенджер</h1><p className="text-xs text-slate-500">Задачи, команды и личные диалоги</p></div><button type="button" onClick={() => setCreateOpen(true)} className="rounded-lg bg-blue-600 p-2 text-white" aria-label="Новый чат"><Plus className="h-4 w-4" /></button></div>
        <div className="p-3"><div className="flex items-center gap-2 rounded-lg border border-slate-200 px-2"><Search className="h-4 w-4 text-slate-400" /><input value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="Найти чат" className="h-9 min-w-0 flex-1 border-0 text-sm outline-none" /></div></div>
        <div className="min-h-0 flex-1 overflow-y-auto px-2 pb-2">{loading ? <div className="p-3 text-sm text-slate-500">Загрузка…</div> : filtered.length === 0 ? <div className="p-3 text-sm text-slate-500">Чатов пока нет.</div> : filtered.map((thread) => { const Icon = threadIcon(thread.kind); return <button key={thread.id} type="button" onClick={() => setActiveId(thread.id)} className={`mb-1 flex w-full items-center gap-3 rounded-xl p-2.5 text-left ${activeId === thread.id ? 'bg-blue-50' : 'hover:bg-slate-50'}`}><span className={`inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-full ${thread.kind === 'task' ? 'bg-emerald-100 text-emerald-700' : thread.kind === 'direct' ? 'bg-violet-100 text-violet-700' : 'bg-blue-100 text-blue-700'}`}><Icon className="h-4 w-4" /></span><span className="min-w-0 flex-1"><span className="block truncate text-sm font-semibold text-slate-800">{thread.name}</span><span className="block truncate text-xs text-slate-500">{thread.lastMessagePreview || 'Нет сообщений'}</span></span>{thread.unreadCount > 0 ? <span className="rounded-full bg-blue-600 px-2 py-0.5 text-[11px] font-semibold text-white">{thread.unreadCount}</span> : null}</button>; })}</div>
      </aside>
      {active ? <ChatConversation thread={active} people={people} currentUserId={user.id} onBack={() => setActiveId(null)} onThreadChanged={load} onOpenTask={onOpenTask} /> : <div className="hidden flex-1 items-center justify-center text-sm text-slate-500 lg:flex"><MessageCircle className="mr-2 h-5 w-5" />Выберите чат или создайте новый</div>}
      {createOpen ? <div className="fixed inset-0 z-[90] flex items-center justify-center bg-slate-950/40 p-4"><div className="w-full max-w-lg rounded-2xl bg-white p-4 shadow-2xl"><div className="flex items-center justify-between"><h2 className="font-semibold text-slate-900">Новый чат</h2><button type="button" onClick={() => setCreateOpen(false)}><X className="h-5 w-5" /></button></div><div className="mt-4 grid grid-cols-2 gap-2"><button type="button" onClick={() => { setCreateKind('channel'); setSelectedPeople([]); }} className={`rounded-xl border p-3 text-sm font-semibold ${createKind === 'channel' ? 'border-blue-300 bg-blue-50 text-blue-700' : 'border-slate-200'}`}><Users className="mx-auto mb-1 h-5 w-5" />Командный канал</button><button type="button" onClick={() => { setCreateKind('direct'); setSelectedPeople([]); }} className={`rounded-xl border p-3 text-sm font-semibold ${createKind === 'direct' ? 'border-violet-300 bg-violet-50 text-violet-700' : 'border-slate-200'}`}><UserRound className="mx-auto mb-1 h-5 w-5" />Личный диалог</button></div>{createKind === 'channel' ? <input value={channelName} onChange={(event) => setChannelName(event.target.value)} placeholder="Название канала" maxLength={200} className="mt-3 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /> : null}<div className="mt-3 max-h-64 overflow-y-auto rounded-xl border border-slate-200 p-2">{people.filter((person) => person.id !== user.id).map((person) => { const selected = selectedPeople.includes(person.id); return <label key={person.id} className="flex cursor-pointer items-center gap-3 rounded-lg px-2 py-2 hover:bg-slate-50"><input type={createKind === 'direct' ? 'radio' : 'checkbox'} checked={selected} onChange={() => setSelectedPeople((current) => createKind === 'direct' ? [person.id] : selected ? current.filter((id) => id !== person.id) : [...current, person.id])} /><span className="min-w-0"><span className="block truncate text-sm font-medium text-slate-800">{person.name}</span><span className="block truncate text-xs text-slate-500">{person.email}</span></span></label>; })}</div><button type="button" onClick={() => void create()} disabled={creating || selectedPeople.length === 0 || (createKind === 'channel' && !channelName.trim())} className="mt-4 w-full rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white disabled:bg-slate-300">{creating ? 'Создаём...' : 'Открыть чат'}</button></div></div> : null}
    </div>
  );
}

export function TaskChatPanel({ taskId, onOpenTask }: { taskId: number; onOpenTask?: (taskId: number) => void }) {
  const { user } = useAuth();
  const toast = useToast();
  const [thread, setThread] = useState<InternalChatThread | null>(null);
  const [people, setPeople] = useState<InternalChatPerson[]>([]);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let cancelled = false;
    Promise.all([internalChatApi.openTask(taskId), internalChatApi.getPeople()])
      .then(([nextThread, nextPeople]) => { if (!cancelled) { setThread(nextThread); setPeople(nextPeople); } })
      .catch((reason) => { const message = errorMessage(reason); if (!cancelled) setError(message); toast.error(message); });
    return () => { cancelled = true; };
  }, [taskId, toast]);
  if (!user) return null;
  if (error) return <div className="rounded-xl border border-rose-200 bg-rose-50 p-3 text-sm text-rose-700">{error}</div>;
  if (!thread) return <div className="flex items-center justify-center p-5 text-sm text-slate-500"><Loader2 className="mr-2 h-4 w-4 animate-spin" />Открываем чат задачи</div>;
  return <ChatConversation thread={thread} people={people} currentUserId={user.id} compact onOpenTask={onOpenTask} />;
}
