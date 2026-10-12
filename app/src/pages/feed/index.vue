<script lang="ts" setup>
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useApiStore } from '@/store';
import FeedPlayer from './FeedPlayer.vue';
import type { FeedItem } from './FeedPlayer.vue';

// 与后端 DouyinFeedSummaryDto 保持一致
interface FeedSummary {
  accounts: { id: string; name: string }[];
  total: number;
  favorite: number;
  collect: number;
  follow: number;
  mix: number;
  series: number;
  authors: number;
}

// 与后端 DouyinFeedAuthorDto 保持一致
interface FeedAuthor {
  authorId: string;
  name?: string;
  count: number;
  latestSyncTime?: string;
  sampleVideoId?: string;
}

type VideoOrder = 'latest' | 'publish' | 'random';
type AuthorOrder = 'count' | 'latest' | 'name';
type HomeTab = 'authors' | 'videos';

const PAGE_SIZE = 30;
const AUTHOR_PAGE_SIZE = 36;
const VIDEO_ORDERS: { key: VideoOrder; label: string }[] = [
  { key: 'latest', label: '最新同步' },
  { key: 'publish', label: '最新发布' },
  { key: 'random', label: '随机' },
];
const AUTHOR_ORDERS: { key: AuthorOrder; label: string }[] = [
  { key: 'count', label: '视频最多' },
  { key: 'latest', label: '最近更新' },
  { key: 'name', label: '名称' },
];
const TYPE_LABELS: { key: keyof FeedSummary; label: string }[] = [
  { key: 'favorite', label: '喜欢' },
  { key: 'collect', label: '收藏' },
  { key: 'follow', label: '关注' },
  { key: 'mix', label: '合集' },
  { key: 'series', label: '短剧' },
];

const emptySummary = (): FeedSummary => ({
  accounts: [],
  total: 0,
  favorite: 0,
  collect: 0,
  follow: 0,
  mix: 0,
  series: 0,
  authors: 0,
});

const route = useRoute();
const router = useRouter();
const rootEl = ref<HTMLElement | null>(null);

// 手机端走独立全屏页面（/me），需要自己提供返回入口；桌面端嵌在后台布局里
const standalone = computed(() => route.path === '/me');

interface CurrentAuthor {
  id: string;
  name?: string;
  // 这位博主的某一条视频，用来取头像
  sample?: string;
}

const queryValue = (value: unknown) => (Array.isArray(value) ? String(value[0] ?? '') : String(value ?? ''));

// 当前看的是哪位博主记在浏览器的历史记录里：后退键能回到博主列表，
// 地址本身不变，后台顶部的标签栏也就不会给每位博主各开一个标签。
// 直接打开带 author 参数的链接同样有效。
function readCurrentAuthor(): CurrentAuthor | null {
  const state = window.history.state?.feedAuthor;
  if (state?.id) return { id: String(state.id), name: state.name || undefined, sample: state.sample || undefined };
  const id = queryValue(route.query.author);
  return id ? { id, name: queryValue(route.query.name) || undefined } : null;
}

const currentAuthor = ref<CurrentAuthor | null>(readCurrentAuthor());
const authorId = computed(() => currentAuthor.value?.id || '');
const inProfile = computed(() => !!authorId.value);

function syncAuthorFromHistory() {
  currentAuthor.value = readCurrentAuthor();
}

const cookieId = ref('');
const summary = ref<FeedSummary>(emptySummary());
const homeTab = ref<HomeTab>('authors');

// ===== 博主列表 =====
const authors = ref<FeedAuthor[]>([]);
const authorsTotal = ref(0);
const authorsPage = ref(0);
const authorsLoading = ref(false);
const authorsHasMore = ref(true);
const authorsError = ref('');
const authorOrder = ref<AuthorOrder>('count');
const authorKeywordInput = ref('');
const authorKeyword = ref('');
let authorsVersion = 0;
let lastOpenedAuthorId = '';

// ===== 博主主页 =====
const profile = ref({ name: '', signature: '', douyinNo: '', enterprise: '', icon: '' });
const authorSummary = ref<FeedSummary>(emptySummary());

// ===== 视频列表（全部视频，或某位博主的视频） =====
const items = ref<FeedItem[]>([]);
const total = ref(0);
const pageIndex = ref(0);
const loading = ref(false);
const hasMore = ref(true);
const errorMessage = ref('');
const activeType = ref('');
const order = ref<VideoOrder>('latest');
const seed = ref(newSeed());
const keywordInput = ref('');
const keyword = ref('');
let videosVersion = 0;

const imageFailed = reactive<Record<string, boolean>>({});
const playerOpen = ref(false);
const playerIndex = ref(0);

const loadMoreRef = ref<HTMLElement | null>(null);
let observer: IntersectionObserver | null = null;
let keywordTimer: ReturnType<typeof setTimeout> | undefined;

const showAuthors = computed(() => !inProfile.value && homeTab.value === 'authors');

const accountName = computed(() => {
  if (!cookieId.value) return summary.value.accounts.length === 1 ? summary.value.accounts[0].name : '我的抖音';
  return summary.value.accounts.find((x) => x.id === cookieId.value)?.name || '我的抖音';
});

const authorName = computed(
  () => profile.value.name || currentAuthor.value?.name || items.value[0]?.author || '未知博主'
);

// 博主头像取自他的某一条视频：优先用列表带过来的，其次是已加载的第一条
const authorSampleId = computed(() => currentAuthor.value?.sample || items.value[0]?.id || '');

// 分类标签只显示有内容的；博主主页里如果只有一种分类就不显示这一行
const typeTabs = computed(() => {
  const source = inProfile.value ? authorSummary.value : summary.value;
  const tabs = TYPE_LABELS.map((x) => ({ key: x.key as string, label: x.label, count: source[x.key] as number })).filter(
    (x) => x.count > 0 || x.key === activeType.value
  );
  if (inProfile.value && tabs.length <= 1 && !activeType.value) return [];
  return [{ key: '', label: '全部', count: source.total }, ...tabs];
});

const hasVideoFilter = computed(() => !!keyword.value || !!activeType.value);

function newSeed() {
  return Math.floor(Math.random() * 2147483647);
}

const coverUrl = (videoId: string) => `/api/video/cover/${encodeURIComponent(videoId)}`;
const avatarUrl = (videoId: string) => `/api/video/avatar/${encodeURIComponent(videoId)}`;

async function loadSummary() {
  try {
    const res = await useApiStore().FeedSummary(cookieId.value || undefined);
    summary.value = { ...emptySummary(), ...res.data };
  } catch (error) {
    console.error('加载视频统计失败：', error);
  }
}

// ===== 博主列表 =====
async function loadMoreAuthors() {
  if (authorsLoading.value || !authorsHasMore.value) return;

  const version = authorsVersion;
  authorsLoading.value = true;
  authorsError.value = '';
  try {
    const res = await useApiStore().FeedAuthors({
      cookieId: cookieId.value || undefined,
      keyword: authorKeyword.value || undefined,
      order: authorOrder.value,
      pageIndex: authorsPage.value + 1,
      pageSize: AUTHOR_PAGE_SIZE,
    });
    if (version !== authorsVersion) return;

    const page = (res.data?.data ?? []) as FeedAuthor[];
    const known = new Set(authors.value.map((x) => x.authorId));
    authors.value = [...authors.value, ...page.filter((x) => !known.has(x.authorId))];
    authorsPage.value += 1;
    authorsTotal.value = res.data?.total ?? authors.value.length;
    authorsHasMore.value = page.length > 0 && authorsPage.value * AUTHOR_PAGE_SIZE < authorsTotal.value;
  } catch (error: any) {
    if (version !== authorsVersion) return;
    authorsError.value = error?.message || error?.msg || '加载博主失败，请稍后重试';
  } finally {
    if (version === authorsVersion) {
      authorsLoading.value = false;
      fillViewport();
    }
  }
}

function reloadAuthors() {
  authorsVersion++;
  authors.value = [];
  authorsTotal.value = 0;
  authorsPage.value = 0;
  authorsHasMore.value = true;
  authorsLoading.value = false;
  authorsError.value = '';
  return loadMoreAuthors();
}

function selectAuthorOrder(next: AuthorOrder) {
  if (authorOrder.value === next) return;
  authorOrder.value = next;
  void reloadAuthors();
}

function openAuthor(author: { authorId?: string; name?: string; sampleVideoId?: string }) {
  if (!author.authorId) return;
  lastOpenedAuthorId = author.authorId;
  // 地址不变，只往历史记录里压一条带博主信息的记录（force 允许跳到同一个地址）
  router
    .push({
      path: route.path,
      query: route.query,
      force: true,
      state: {
        feedAuthor: { id: author.authorId, name: author.name || '', sample: author.sampleVideoId || '' },
      },
    })
    .then(syncAuthorFromHistory);
}

// ===== 视频列表 =====
async function loadMoreVideos() {
  if (loading.value || !hasMore.value) return;

  const version = videosVersion;
  loading.value = true;
  errorMessage.value = '';
  try {
    const res = await useApiStore().FeedList({
      type: activeType.value || undefined,
      cookieId: cookieId.value || undefined,
      authorId: authorId.value || undefined,
      keyword: keyword.value || undefined,
      order: order.value,
      seed: seed.value,
      pageIndex: pageIndex.value + 1,
      pageSize: PAGE_SIZE,
    });
    if (version !== videosVersion) return;

    const page = (res.data?.data ?? []) as FeedItem[];
    // 翻页期间有新视频同步进来时，后面的页可能和已加载的重复
    const known = new Set(items.value.map((x) => x.id));
    items.value = [...items.value, ...page.filter((x) => !known.has(x.id))];
    pageIndex.value += 1;
    total.value = res.data?.total ?? items.value.length;
    hasMore.value = page.length > 0 && pageIndex.value * PAGE_SIZE < total.value;
  } catch (error: any) {
    if (version !== videosVersion) return;
    errorMessage.value = error?.message || error?.msg || '加载视频失败，请稍后重试';
  } finally {
    if (version === videosVersion) {
      loading.value = false;
      fillViewport();
    }
  }
}

function reloadVideos() {
  videosVersion++;
  items.value = [];
  total.value = 0;
  pageIndex.value = 0;
  hasMore.value = true;
  loading.value = false;
  errorMessage.value = '';
  return loadMoreVideos();
}

function selectType(type: string) {
  if (activeType.value === type) return;
  activeType.value = type;
  void reloadVideos();
}

function selectVideoOrder(next: VideoOrder) {
  // 再点一次「随机」就是换一批
  if (order.value === next && next !== 'random') return;
  order.value = next;
  if (next === 'random') seed.value = newSeed();
  void reloadVideos();
}

function resetVideoFilters(nextOrder: VideoOrder) {
  activeType.value = '';
  keywordInput.value = '';
  keyword.value = '';
  order.value = nextOrder;
}

// ===== 通用 =====
function loadMore() {
  return showAuthors.value ? loadMoreAuthors() : loadMoreVideos();
}

// 宽屏上一页可能铺不满，这时底部的哨兵元素一直可见、不会再触发观察回调，需要主动接着加载
function fillViewport() {
  requestAnimationFrame(() => {
    const sentinel = loadMoreRef.value;
    if (!sentinel) return;
    if (sentinel.getBoundingClientRect().top < window.innerHeight + 400) void loadMore();
  });
}

function selectHomeTab(tab: HomeTab) {
  if (homeTab.value === tab) return;
  homeTab.value = tab;
  if (tab === 'videos') {
    resetVideoFilters('latest');
    void reloadVideos();
  } else if (authors.value.length === 0) {
    void reloadAuthors();
  }
}

function onAccountChange() {
  void loadSummary();
  void reloadAuthors();
  if (homeTab.value === 'videos') void reloadVideos();
}

function openPlayer(index: number) {
  playerIndex.value = index;
  playerOpen.value = true;
}

// 「刷视频」：博主主页里打乱该博主的视频，首页打乱全部视频，从第一条开始播
async function startShuffle() {
  if (!inProfile.value) {
    // 首页的「刷视频」不受分类和搜索词影响
    activeType.value = '';
    keywordInput.value = '';
    keyword.value = '';
  }
  order.value = 'random';
  seed.value = newSeed();
  await reloadVideos();
  if (items.value.length > 0) openPlayer(0);
}

function onPlayerClose(index: number) {
  playerOpen.value = false;
  const item = items.value[index];
  if (!item || showAuthors.value) return;
  // 回到封面墙时定位到刚才看的那一条
  requestAnimationFrame(() => {
    document.getElementById(`feed-card-${item.id}`)?.scrollIntoView({ block: 'center' });
  });
}

function onPlayerAuthor(item: FeedItem) {
  playerOpen.value = false;
  // 已经在这位博主的主页里就不用再跳了
  if (!item.authorId || item.authorId === authorId.value) return;
  openAuthor({ authorId: item.authorId, name: item.author, sampleVideoId: item.id });
}

function goBack() {
  if (inProfile.value) {
    // 从列表点进来的直接后退；通过带 author 参数的链接进来的，去掉参数回到博主列表
    if (window.history.state?.feedAuthor) router.back();
    else router.replace({ path: route.path }).then(syncAuthorFromHistory);
    return;
  }
  router.push('/mobile');
}

async function loadProfile(id: string) {
  profile.value = { name: '', signature: '', douyinNo: '', enterprise: '', icon: '' };
  authorSummary.value = emptySummary();
  try {
    const [profileRes, summaryRes] = await Promise.all([
      useApiStore().AuthorVideoProfile(id, currentAuthor.value?.name || ''),
      useApiStore().FeedSummary(cookieId.value || undefined, id),
    ]);
    // 等待期间可能已经切到别的博主
    if (id !== authorId.value) return;
    if (profileRes?.data) profile.value = { ...profile.value, ...profileRes.data };
    if (summaryRes?.data) authorSummary.value = { ...emptySummary(), ...summaryRes.data };
  } catch (error) {
    console.error('加载博主资料失败：', error);
  }
}

watch(
  authorId,
  (id, previous) => {
    playerOpen.value = false;
    if (id) {
      // 博主主页默认按发布时间倒序，和抖音一致
      resetVideoFilters('publish');
      void loadProfile(id);
      void reloadVideos();
      nextTick(() => rootEl.value?.scrollIntoView({ block: 'start' }));
      return;
    }

    if (previous === undefined) return;
    // 从博主主页回到首页
    resetVideoFilters('latest');
    if (homeTab.value === 'videos') {
      void reloadVideos();
    } else {
      videosVersion++;
      items.value = [];
      if (authors.value.length === 0) void reloadAuthors();
      nextTick(() => {
        // 回到刚才点开的那位博主的位置；找不到（比如是直接打开链接进来的）就回到顶部
        const card = lastOpenedAuthorId ? document.getElementById(`feed-author-${lastOpenedAuthorId}`) : null;
        if (card) card.scrollIntoView({ block: 'center' });
        else rootEl.value?.scrollIntoView({ block: 'start' });
      });
    }
  },
  { immediate: true }
);

function debounceKeyword(apply: () => void) {
  if (keywordTimer) clearTimeout(keywordTimer);
  keywordTimer = setTimeout(apply, 350);
}

watch(keywordInput, (value) => {
  debounceKeyword(() => {
    const next = value.trim();
    if (next === keyword.value) return;
    keyword.value = next;
    void reloadVideos();
  });
});

watch(authorKeywordInput, (value) => {
  debounceKeyword(() => {
    const next = value.trim();
    if (next === authorKeyword.value) return;
    authorKeyword.value = next;
    void reloadAuthors();
  });
});

watch(loadMoreRef, (element) => {
  observer?.disconnect();
  if (element) observer?.observe(element);
});

onMounted(() => {
  observer = new IntersectionObserver(
    (entries) => {
      if (entries[0]?.isIntersecting) void loadMore();
    },
    { rootMargin: '400px 0px' }
  );
  if (loadMoreRef.value) observer.observe(loadMoreRef.value);
  // 浏览器前进/后退时，按历史记录里的信息切换博主主页和列表
  window.addEventListener('popstate', syncAuthorFromHistory);
  void loadSummary();
  if (!inProfile.value) void loadMoreAuthors();
});

onBeforeUnmount(() => {
  window.removeEventListener('popstate', syncAuthorFromHistory);
  observer?.disconnect();
  if (keywordTimer) clearTimeout(keywordTimer);
});
</script>

<template>
  <main ref="rootEl" class="my-douyin" :class="{ standalone }">
    <!-- 博主主页的头部 -->
    <header v-if="inProfile" class="md-profile">
      <button type="button" class="md-profile-back" aria-label="返回博主列表" @click="goBack">
        <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="m15 18-6-6 6-6" /></svg>
      </button>

      <div class="md-profile-avatar">
        <img
          v-if="authorSampleId && !imageFailed[`avatar-${authorSampleId}`]"
          :src="avatarUrl(authorSampleId)"
          :alt="authorName"
          referrerpolicy="no-referrer"
          @error="imageFailed[`avatar-${authorSampleId}`] = true"
        />
        <span v-else>{{ authorName.slice(0, 1) }}</span>
      </div>

      <div class="md-profile-main">
        <h1>
          {{ authorName }}
          <span v-if="profile.enterprise" class="md-profile-verified">{{ profile.enterprise }}</span>
        </h1>
        <p v-if="profile.signature" class="md-profile-signature">{{ profile.signature }}</p>
        <p class="md-profile-stats">
          <span><strong>{{ authorSummary.total || total }}</strong> 个本地视频</span>
          <span v-if="profile.douyinNo">抖音号 {{ profile.douyinNo }}</span>
        </p>
      </div>

      <button type="button" class="md-profile-shuffle" :disabled="total === 0 && !loading" @click="startShuffle">
        <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M8 5.14v13.72a1 1 0 0 0 1.53.85l10.9-6.86a1 1 0 0 0 0-1.7L9.53 4.29A1 1 0 0 0 8 5.14z" /></svg>
        随机播放
      </button>
    </header>

    <!-- 首页的头部 -->
    <header v-else class="md-profile">
      <button v-if="standalone" type="button" class="md-profile-back" aria-label="返回" @click="goBack">
        <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="m15 18-6-6 6-6" /></svg>
      </button>

      <div class="md-profile-avatar"><span>{{ accountName.slice(0, 1) }}</span></div>

      <div class="md-profile-main">
        <h1>{{ accountName }}</h1>
        <p class="md-profile-stats">
          <span><strong>{{ summary.authors }}</strong> 位博主</span>
          <span><strong>{{ summary.total }}</strong> 个本地视频</span>
        </p>
        <select v-if="summary.accounts.length > 1" v-model="cookieId" class="md-profile-account" aria-label="切换账号" @change="onAccountChange">
          <option value="">全部账号</option>
          <option v-for="account in summary.accounts" :key="account.id" :value="account.id">{{ account.name }}</option>
        </select>
      </div>

      <button type="button" class="md-profile-shuffle" :disabled="summary.total === 0" @click="startShuffle">
        <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M8 5.14v13.72a1 1 0 0 0 1.53.85l10.9-6.86a1 1 0 0 0 0-1.7L9.53 4.29A1 1 0 0 0 8 5.14z" /></svg>
        刷视频
      </button>
    </header>

    <!-- 首页：博主 / 全部视频 -->
    <nav v-if="!inProfile" class="md-tabs" role="tablist" aria-label="浏览方式">
      <button type="button" role="tab" class="md-tab" :class="{ active: homeTab === 'authors' }" :aria-selected="homeTab === 'authors'" @click="selectHomeTab('authors')">
        博主 <span>{{ summary.authors }}</span>
      </button>
      <button type="button" role="tab" class="md-tab" :class="{ active: homeTab === 'videos' }" :aria-selected="homeTab === 'videos'" @click="selectHomeTab('videos')">
        全部视频 <span>{{ summary.total }}</span>
      </button>
    </nav>

    <!-- ===== 博主列表 ===== -->
    <template v-if="showAuthors">
      <div class="md-toolbar">
        <div class="md-orders" role="group" aria-label="排序方式">
          <button v-for="item in AUTHOR_ORDERS" :key="item.key" type="button" class="md-order" :class="{ active: authorOrder === item.key }" @click="selectAuthorOrder(item.key)">
            {{ item.label }}
          </button>
        </div>
        <label class="md-search">
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="7" /><path d="m20 20-3.5-3.5" /></svg>
          <input v-model="authorKeywordInput" type="search" placeholder="搜索博主" autocomplete="off" />
        </label>
      </div>

      <section class="md-authors" aria-label="博主列表">
        <article
          v-for="author in authors"
          :id="`feed-author-${author.authorId}`"
          :key="author.authorId"
          class="md-author"
          role="button"
          tabindex="0"
          @click="openAuthor(author)"
          @keydown.enter="openAuthor(author)"
        >
          <img
            v-if="author.sampleVideoId && !imageFailed[`cover-${author.sampleVideoId}`]"
            class="md-author-cover"
            :src="coverUrl(author.sampleVideoId)"
            alt=""
            loading="lazy"
            referrerpolicy="no-referrer"
            @error="imageFailed[`cover-${author.sampleVideoId}`] = true"
          />
          <div class="md-author-info">
            <div class="md-author-avatar">
              <img
                v-if="author.sampleVideoId && !imageFailed[`avatar-${author.sampleVideoId}`]"
                :src="avatarUrl(author.sampleVideoId)"
                alt=""
                loading="lazy"
                referrerpolicy="no-referrer"
                @error="imageFailed[`avatar-${author.sampleVideoId}`] = true"
              />
              <span v-else>{{ (author.name || '博').slice(0, 1) }}</span>
            </div>
            <strong>{{ author.name || '未知博主' }}</strong>
            <span>{{ author.count }} 个视频</span>
          </div>
        </article>
      </section>

      <div v-if="authorsLoading && authors.length === 0" class="md-state">正在加载博主…</div>
      <div v-else-if="!authorsLoading && authors.length === 0 && !authorsError" class="md-state">
        <strong>{{ authorKeyword ? '没有找到这位博主' : '这里还没有可以播放的视频' }}</strong>
        <span>{{ authorKeyword ? '换个名字试试' : '同步任务把视频下载到本地后，博主会显示在这里' }}</span>
      </div>
      <div v-if="authorsError" class="md-state error">
        <span>{{ authorsError }}</span>
        <button type="button" @click="loadMoreAuthors">重试</button>
      </div>
      <div v-if="authors.length > 0" ref="loadMoreRef" class="md-load-more">
        <span v-if="authorsLoading">加载中…</span>
        <span v-else-if="!authorsHasMore">已显示全部 {{ authors.length }} 位博主</span>
      </div>
    </template>

    <!-- ===== 视频封面墙：全部视频，或某位博主的视频 ===== -->
    <template v-else>
      <nav v-if="typeTabs.length > 0" class="md-types" aria-label="视频分类">
        <button v-for="tab in typeTabs" :key="tab.key" type="button" class="md-type" :class="{ active: activeType === tab.key }" @click="selectType(tab.key)">
          {{ tab.label }} <span>{{ tab.count }}</span>
        </button>
      </nav>

      <div class="md-toolbar">
        <div class="md-orders" role="group" aria-label="排序方式">
          <button v-for="item in VIDEO_ORDERS" :key="item.key" type="button" class="md-order" :class="{ active: order === item.key }" @click="selectVideoOrder(item.key)">
            {{ item.label }}
          </button>
        </div>
        <label class="md-search">
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="7" /><path d="m20 20-3.5-3.5" /></svg>
          <input v-model="keywordInput" type="search" :placeholder="inProfile ? '搜索这位博主的视频' : '搜索标题或博主'" autocomplete="off" />
        </label>
      </div>

      <section class="md-grid" aria-label="视频列表">
        <article
          v-for="(item, index) in items"
          :id="`feed-card-${item.id}`"
          :key="item.id"
          class="md-card"
          role="button"
          tabindex="0"
          @click="openPlayer(index)"
          @keydown.enter="openPlayer(index)"
        >
          <img
            v-if="!imageFailed[`cover-${item.id}`]"
            :src="coverUrl(item.id)"
            :alt="item.title || '视频封面'"
            loading="lazy"
            referrerpolicy="no-referrer"
            @error="imageFailed[`cover-${item.id}`] = true"
          />
          <div v-else class="md-card-placeholder" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="34" height="34" fill="currentColor"><path d="M8 5.14v13.72a1 1 0 0 0 1.53.85l10.9-6.86a1 1 0 0 0 0-1.7L9.53 4.29A1 1 0 0 0 8 5.14z" /></svg>
          </div>

          <span v-if="!activeType && item.typeName" class="md-card-badge">{{ item.typeName }}</span>
          <div class="md-card-copy">
            <p>{{ item.title || '无标题' }}</p>
            <span v-if="!inProfile">@{{ item.author || '未知博主' }}</span>
          </div>
        </article>
      </section>

      <div v-if="loading && items.length === 0" class="md-state">正在加载视频…</div>
      <div v-else-if="!loading && items.length === 0 && !errorMessage" class="md-state">
        <strong>{{ hasVideoFilter ? '没有符合条件的视频' : '这里还没有可以播放的视频' }}</strong>
        <span>{{ hasVideoFilter ? '换个关键词或分类试试' : '同步任务把视频下载到本地后会显示在这里' }}</span>
      </div>
      <div v-if="errorMessage" class="md-state error">
        <span>{{ errorMessage }}</span>
        <button type="button" @click="loadMoreVideos">重试</button>
      </div>
      <div v-if="items.length > 0" ref="loadMoreRef" class="md-load-more">
        <span v-if="loading">加载中…</span>
        <span v-else-if="!hasMore">已显示全部 {{ items.length }} 个视频</span>
      </div>
    </template>

    <FeedPlayer
      v-if="playerOpen"
      :items="items"
      :start-index="playerIndex"
      :total="total"
      :has-more="hasMore"
      @close="onPlayerClose"
      @load-more="loadMoreVideos"
      @author="onPlayerAuthor"
    />
  </main>
</template>

<style scoped lang="less">
@accent: #fe2c55;
@panel: #252632;

.my-douyin {
  min-height: calc(100vh - 140px);
  padding: 20px clamp(12px, 3vw, 32px) 40px;
  color: #f1f2f6;
  border-radius: 16px;
  background: #161823;

  &.standalone {
    min-height: 100%;
    border-radius: 0;
  }
}

.md-profile {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 8px 0 20px;
}

.md-profile-back {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  padding: 0;
  flex: 0 0 auto;
  color: #ffffff;
  border: 0;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.1);
  cursor: pointer;

  &:hover {
    background: rgba(255, 255, 255, 0.18);
  }
}

.md-profile-avatar {
  display: grid;
  place-items: center;
  width: 84px;
  height: 84px;
  flex: 0 0 auto;
  overflow: hidden;
  color: #ffffff;
  border: 2px solid rgba(255, 255, 255, 0.85);
  border-radius: 50%;
  background: linear-gradient(135deg, @accent, #7a5cff);
  font-size: 32px;
  font-weight: 700;

  img {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }
}

.md-profile-main {
  min-width: 0;
  flex: 1;
  text-align: left;

  h1 {
    margin: 0;
    overflow: hidden;
    color: #ffffff;
    font-size: 22px;
    font-weight: 700;
    white-space: nowrap;
    text-overflow: ellipsis;
  }
}

.md-profile-verified {
  margin-left: 8px;
  padding: 2px 8px;
  color: #ffd27a;
  border-radius: 4px;
  background: rgba(255, 210, 122, 0.14);
  font-size: 12px;
  font-weight: 400;
  vertical-align: middle;
}

.md-profile-signature {
  display: -webkit-box;
  max-width: 720px;
  margin: 6px 0 0;
  overflow: hidden;
  color: rgba(255, 255, 255, 0.72);
  font-size: 13px;
  line-height: 1.5;
  white-space: pre-line;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.md-profile-stats {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 16px;
  margin: 6px 0 0;
  color: rgba(255, 255, 255, 0.6);
  font-size: 13px;

  strong {
    color: #ffffff;
    font-size: 15px;
  }
}

.md-profile-account {
  max-width: 100%;
  margin-top: 8px;
  padding: 4px 8px;
  color: #ffffff;
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 6px;
  background: @panel;
  font-size: 13px;
}

.md-profile-shuffle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 10px 22px;
  flex: 0 0 auto;
  color: #ffffff;
  border: 0;
  border-radius: 999px;
  background: @accent;
  font-size: 15px;
  font-weight: 700;
  cursor: pointer;

  &:hover:not(:disabled) {
    background: #ff4d70;
  }

  &:disabled {
    opacity: 0.45;
    cursor: default;
  }
}

.md-tabs {
  position: sticky;
  top: 0;
  z-index: 5;
  display: flex;
  gap: 4px;
  overflow-x: auto;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
  background: #161823;
  scrollbar-width: none;

  &::-webkit-scrollbar {
    display: none;
  }
}

.md-tab {
  position: relative;
  padding: 12px 14px;
  flex: 0 0 auto;
  color: rgba(255, 255, 255, 0.55);
  border: 0;
  background: none;
  font-size: 16px;
  font-weight: 600;
  cursor: pointer;

  span {
    margin-left: 2px;
    font-size: 12px;
    font-weight: 400;
  }

  &.active {
    color: #ffffff;

    &::after {
      position: absolute;
      right: 14px;
      bottom: 0;
      left: 14px;
      height: 2px;
      border-radius: 2px;
      background: #ffffff;
      content: '';
    }
  }
}

.md-types {
  display: flex;
  gap: 8px;
  padding-top: 14px;
  overflow-x: auto;
  scrollbar-width: none;

  &::-webkit-scrollbar {
    display: none;
  }
}

.md-type {
  padding: 5px 14px;
  flex: 0 0 auto;
  color: rgba(255, 255, 255, 0.7);
  border: 0;
  border-radius: 999px;
  background: @panel;
  font-size: 13px;
  cursor: pointer;

  span {
    margin-left: 2px;
    font-size: 11px;
    opacity: 0.7;
  }

  &.active {
    color: #161823;
    background: #ffffff;
    font-weight: 600;
  }
}

.md-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 14px 0 10px;
}

.md-orders {
  display: inline-flex;
  padding: 3px;
  border-radius: 8px;
  background: @panel;
}

.md-order {
  padding: 5px 12px;
  color: rgba(255, 255, 255, 0.6);
  border: 0;
  border-radius: 6px;
  background: none;
  font-size: 13px;
  cursor: pointer;

  &.active {
    color: #ffffff;
    background: rgba(255, 255, 255, 0.14);
  }
}

.md-search {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  padding: 0 10px;
  flex: 0 1 260px;
  color: rgba(255, 255, 255, 0.5);
  border-radius: 8px;
  background: @panel;

  input {
    width: 100%;
    min-width: 0;
    padding: 7px 0;
    color: #ffffff;
    border: 0;
    outline: 0;
    background: none;
    font-size: 13px;

    &::placeholder {
      color: rgba(255, 255, 255, 0.38);
    }
  }
}

// ===== 博主卡片 =====
.md-authors {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(170px, 1fr));
  gap: 10px;
}

.md-author {
  position: relative;
  display: flex;
  align-items: flex-end;
  aspect-ratio: 3 / 4;
  overflow: hidden;
  border-radius: 10px;
  background: linear-gradient(160deg, #2c2d3c, #1d1e2a);
  cursor: pointer;

  &:hover .md-author-cover {
    transform: scale(1.05);
  }

  &:focus-visible {
    outline: 2px solid @accent;
    outline-offset: 2px;
  }
}

// 用这位博主最近一条视频的封面做卡片背景
.md-author-cover {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: cover;
  transition: transform 0.25s ease;
}

.md-author-info {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  width: 100%;
  padding: 44px 10px 12px;
  text-align: center;
  background: linear-gradient(to top, rgba(0, 0, 0, 0.86) 35%, rgba(0, 0, 0, 0));

  strong {
    max-width: 100%;
    overflow: hidden;
    color: #ffffff;
    font-size: 14px;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  > span {
    color: rgba(255, 255, 255, 0.7);
    font-size: 12px;
  }
}

.md-author-avatar {
  display: grid;
  place-items: center;
  width: 58px;
  height: 58px;
  overflow: hidden;
  color: #ffffff;
  border: 2px solid #ffffff;
  border-radius: 50%;
  background: linear-gradient(135deg, @accent, #7a5cff);
  font-size: 22px;
  font-weight: 700;

  img {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }
}

// ===== 视频封面墙 =====
.md-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(170px, 1fr));
  gap: 8px;
}

.md-card {
  position: relative;
  aspect-ratio: 3 / 4;
  overflow: hidden;
  border-radius: 8px;
  background: @panel;
  cursor: pointer;

  img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    transition: transform 0.25s ease;
  }

  &:hover img {
    transform: scale(1.04);
  }

  &:focus-visible {
    outline: 2px solid @accent;
    outline-offset: 2px;
  }
}

.md-card-placeholder {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
  padding-bottom: 40px;
  color: rgba(255, 255, 255, 0.22);
  background: linear-gradient(160deg, #2c2d3c, #1d1e2a);
}

.md-card-badge {
  position: absolute;
  top: 6px;
  left: 6px;
  padding: 2px 7px;
  color: #ffffff;
  border-radius: 4px;
  background: rgba(0, 0, 0, 0.5);
  font-size: 11px;
}

.md-card-copy {
  position: absolute;
  right: 0;
  bottom: 0;
  left: 0;
  padding: 28px 8px 8px;
  text-align: left;
  background: linear-gradient(to top, rgba(0, 0, 0, 0.78), rgba(0, 0, 0, 0));

  p {
    display: -webkit-box;
    margin: 0;
    overflow: hidden;
    color: #ffffff;
    font-size: 12px;
    line-height: 1.4;
    word-break: break-word;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 2;
  }

  span {
    display: block;
    margin-top: 3px;
    overflow: hidden;
    color: rgba(255, 255, 255, 0.65);
    font-size: 11px;
    white-space: nowrap;
    text-overflow: ellipsis;
  }
}

.md-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 64px 16px;
  color: rgba(255, 255, 255, 0.55);
  font-size: 13px;
  text-align: center;

  strong {
    color: #ffffff;
    font-size: 16px;
  }

  &.error {
    padding: 24px 16px;
    color: #ff8a9f;
  }

  button {
    padding: 6px 18px;
    color: #ffffff;
    border: 0;
    border-radius: 999px;
    background: rgba(255, 255, 255, 0.14);
    cursor: pointer;
  }
}

.md-load-more {
  padding: 22px 0 4px;
  color: rgba(255, 255, 255, 0.4);
  font-size: 12px;
  text-align: center;
}

@media (max-width: 640px) {
  .my-douyin {
    padding: 12px 6px 32px;
  }

  .md-profile {
    gap: 10px;
    padding: 4px 6px 14px;
  }

  .md-profile-avatar {
    width: 60px;
    height: 60px;
    font-size: 24px;
  }

  .md-profile-main h1 {
    font-size: 18px;
  }

  .md-profile-shuffle {
    padding: 8px 14px;
    font-size: 14px;
  }

  .md-types,
  .md-toolbar {
    padding-right: 6px;
    padding-left: 6px;
  }

  .md-search {
    flex: 1 1 100%;
  }

  .md-authors {
    grid-template-columns: repeat(3, 1fr);
    gap: 4px;
  }

  .md-author {
    border-radius: 4px;
  }

  .md-author-info {
    padding: 30px 4px 8px;
  }

  .md-author-avatar {
    width: 44px;
    height: 44px;
    font-size: 18px;
  }

  .md-author-info strong {
    font-size: 12px;
  }

  // 手机上和抖音一样三列
  .md-grid {
    grid-template-columns: repeat(3, 1fr);
    gap: 2px;
  }

  .md-card {
    border-radius: 2px;
  }

  .md-card-copy span {
    display: none;
  }
}
</style>
