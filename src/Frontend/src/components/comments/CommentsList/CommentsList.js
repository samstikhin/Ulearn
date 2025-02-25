import React, { Component } from "react";
import PropTypes from "prop-types";
import { userType, userRoles, commentPolicy } from "../commonPropTypes";
import { TransitionGroup, CSSTransition } from "react-transition-group";
import { debounce } from "debounce";
import { CommentLite } from "icons";
import { Toast } from "ui";
import CourseLoader from "src/components/course/Course/CourseLoader/CourseLoader";
import Thread from "../Thread/Thread";
import CommentSendForm from "../CommentSendForm/CommentSendForm";
import Error404 from "../../common/Error/Error404";
import Stub from "../Stub/Stub";
import scrollToView from "src/utils/scrollToView";

import styles from "./CommentsList.less";
import { TabsType } from "src/consts/tabsType";

const defaultCommentsData = {
	commentsPerPack: 15,
	scrollDistance: 500,
	threadsToRender: [],
	repliesToRender: [],
};

function throttle(fn, wait) {
	let time = Date.now();
	return function () {
		if((time + wait - Date.now()) < 0) {
			fn();
			time = Date.now();
		}
	}
}

class CommentsList extends Component {
	constructor(props) {
		super(props);

		this.commentsData = { ...defaultCommentsData };

		this.state = {
			newCommentId: 1,
			saveCommentLikeStatus: null,
			threads: [],
			commentEditing: {
				commentId: null,
				sending: false
			},
			reply: {
				commentId: null,
				sending: false
			},
			showFocus: false,
			sending: false,
			loadingComments: false,
			status: "",
			animation: false,
		};

		this.lastThreadRef = React.createRef();
		this.commentsListRef = React.createRef();

		this.debouncedSendData = debounce(this.sendData, 300);
	}

	componentDidMount() {
		const { courseId, slideId, forInstructors } = this.props;
		this.loadComments(courseId, slideId, forInstructors)
			.then(() => {
				if(window.location.hash.includes("#comment")) {
					this.handleScrollToCommentByHashFormUrl();
				}
			});

		this.throttleScroll = throttle(this.handleScrollToBottom, 100);

		window.addEventListener("scroll", this.throttleScroll);
		window.addEventListener("hashchange", this.handleScrollToCommentByHashFormUrl);
	};

	componentWillUnmount() {
		window.removeEventListener("scroll", this.throttleScroll);
		window.removeEventListener("hashchange", this.handleScrollToCommentByHashFormUrl);
	}

	componentDidUpdate(prevProps, prevState) {
		if(this.props.slideId !== prevProps.slideId) {
			this.commentsData = { ...defaultCommentsData };
			this.setStateIfMounted({
				threads: [],
			});
			const { courseId, slideId, forInstructors } = this.props;
			this.loadComments(courseId, slideId, forInstructors);
		}
	}

	get commentIds() {
		const { threads } = this.state;
		return this.getAllCommentsIds(threads);
	}

	getAllCommentsIds(threads) {
		const commentIds = [];

		for (let i = 0; i < threads.length; i++) {
			const thread = threads[i];
			commentIds.push(thread.id);

			if(!thread.replies) continue;

			for (let j = 0; j < thread.replies.length; j++) {
				const reply = thread.replies[j];
				commentIds.push(reply.id);
			}
		}

		return commentIds;
	}

	loadComments = (courseId, slideId, forInstructors, offset, count) => {
		if(this.state.loadingComments) {
			return;
		}

		this.setStateIfMounted({
			loadingComments: true,
		});

		const commentsApiRequest = this.props.commentsApi.getComments(courseId, slideId, forInstructors, offset, count)
			.then(json => {
				const comments = json.topLevelComments;

				this.setStateIfMounted({
					loadingComments: false,
				});
				this.commentsData.threadsToRender = comments;

				this.renderPackOfComments();
				return comments;
			});

		commentsApiRequest
			.catch(() => {
				this.setStateIfMounted({
					status: "error",
				});
			})
			.finally(() => {
				this.setStateIfMounted({
					loadingComments: false,
				});
			});

		return commentsApiRequest;
	};

	renderPackOfComments() {
		const { threadsToRender, repliesToRender, commentsPerPack } = this.commentsData;
		const { threads } = this.state;
		const newThreads = [...threads];
		let countOfCommentsToRender = 0;
		if(repliesToRender.length > 0) {
			const lastThread = newThreads[newThreads.length - 1];
			const newReplies = repliesToRender.splice(0, commentsPerPack);
			countOfCommentsToRender += newReplies.length;
			lastThread.replies = [...lastThread.replies, ...newReplies];
			lastThread.replies.sort((r1, r2) => r1.publishTime - r2.publishTime);
		}

		while (countOfCommentsToRender < commentsPerPack && threadsToRender.length !== 0) {
			const thread = threadsToRender.shift();
			countOfCommentsToRender++;
			const threadReplies = thread.replies;
			let countOfCommentsLeftInPack = commentsPerPack - countOfCommentsToRender;

			if(threadReplies.length > countOfCommentsLeftInPack) {
				thread.replies = threadReplies.splice(0, countOfCommentsLeftInPack);
				countOfCommentsToRender += countOfCommentsLeftInPack;
				newThreads.push(thread);
				this.commentsData.repliesToRender = threadReplies;
			} else {
				countOfCommentsToRender += thread.replies.length;
				newThreads.push(thread);
			}
		}

		this.setStateIfMounted({
			threads: newThreads,
		});
	}

	setStateIfMounted(updater, callback) {
		if(this.commentsListRef.current) {
			this.setState(updater, callback);
		}
	}

	handleScrollToBottom = () => {
		const { scrollDistance } = this.commentsData;

		const element = document.documentElement;
		const windowRelativeBottom = element.getBoundingClientRect().bottom;
		if(windowRelativeBottom < element.clientHeight + scrollDistance) {
			this.renderPackOfComments();
		}
	};

	handleScrollToCommentByHashFormUrl = () => {
		const { courseId, slideId, forInstructors, handleTabChange } = this.props;

		if(window.location.hash.includes("#comment")) {
			const startIndex = window.location.hash.indexOf('-') + 1;
			const commentIdFromHash = +window.location.hash.slice(startIndex);
			const nameChangesTab = forInstructors ? TabsType.allComments : TabsType.instructorsComments;

			const { threadsToRender, repliesToRender } = this.commentsData;
			const notRenderedComments = [...repliesToRender, ...threadsToRender];
			const notRenderedCommentIds = this.getAllCommentsIds(notRenderedComments);
			const allCommentIds = [...this.commentIds, ...notRenderedCommentIds];
			const indexOfComment = notRenderedCommentIds.indexOf(commentIdFromHash);
			if(indexOfComment > 0) {
				const commentsPerPack = this.commentsData.commentsPerPack;
				this.commentsData.commentsPerPack = indexOfComment + commentsPerPack;
				this.renderPackOfComments();
				this.commentsData.commentsPerPack = commentsPerPack;
			} else if(!allCommentIds.includes(commentIdFromHash)) {
				handleTabChange(nameChangesTab, false);
				this.loadComments(courseId, slideId, forInstructors);
				this.setStateIfMounted({
					threads: [],
				});
			}
		}
	};

	render() {
		const { threads, loadingComments } = this.state;
		const { user, courseId, slideId, commentPolicy, key, } = this.props;
		const replies = threads.reduce((sum, current) => sum + current.replies.length, 0);

		if(this.state.status === "error") {
			return <Error404/>;
		}

		if(!courseId || !slideId) {
			return null;
		}

		return (
			<div key={ key } ref={ this.commentsListRef }>
				{ loadingComments ?
					<CourseLoader isSlideLoader={ false }/> :
					<>
						{ !user.id &&
						<Stub hasThreads={ threads.length > 0 } courseId={ courseId } slideId={ slideId }/> }
						{ (user.id && !commentPolicy.areCommentsEnabled) && this.renderMessageIfCommentsDisabled() }
						{ this.renderSendForm() }
						{ this.renderThreads() }
					</> }
				{ (commentPolicy.areCommentsEnabled && user.id && (threads.length + replies) > 7) &&
				<button className={ styles.sendButton } onClick={ this.handleShowSendForm }>
					<CommentLite color="#3072C4"/>
					Оставить комментарий
				</button> }
			</div>
		)
	}

	renderMessageIfCommentsDisabled() {
		return (
			<div className={ styles.textForDisabledComments }>
				В данный момент комментарии выключены.
			</div>
		)
	};

	renderSendForm() {
		const { sending, status, newCommentId, showFocus } = this.state;
		const { user, forInstructors, commentPolicy } = this.props;
		const focusedSendForm = { inSendForm: showFocus, };

		return (
			user.id && (commentPolicy.areCommentsEnabled || commentPolicy.onlyInstructorsCanReply) &&
			<CommentSendForm
				key={ showFocus }
				isShowFocus={ focusedSendForm }
				isForInstructors={ forInstructors }
				commentId={ newCommentId }
				author={ user }
				handleSubmit={ this.handleAddComment }
				sending={ sending }
				sendStatus={ status }/>
		)
	};

	renderThreads() {
		const { threads, commentEditing, reply, animation } = this.state;
		const { user, userRoles, slideType, courseId, commentPolicy, isSlideReady, } = this.props;
		const transitionStyles = {
			enter: styles.enter,
			exit: styles.exit,
			enterActive: styles.enterActive,
			exitActive: styles.exitActive,
		};

		const duration = {
			enter: 1000,
			exit: 500,
		};

		const actions = {
			handleLikeClick: this.handleLikeClick,
			handleCorrectAnswerMark: this.handleCorrectAnswerMark,
			handleApprovedMark: this.handleApprovedMark,
			handlePinnedToTopMark: this.handlePinnedToTopMark,
			handleEditComment: this.handleEditComment,
			handleAddReplyComment: this.handleAddReplyComment,
			handleDeleteComment: this.handleDeleteComment,
			handleShowEditForm: this.handleShowEditForm,
			handleShowReplyForm: this.handleShowReplyForm,
		};

		return (
			<TransitionGroup enter={ animation }>
				{ threads
					.map((comment) =>
						<CSSTransition
							key={ comment.id }
							appear
							in={ animation }
							mountOnEnter
							unmountOnExit
							classNames={ transitionStyles }
							timeout={ duration }>
							<section className={ styles.thread } key={ comment.id } ref={ this.lastThreadRef }>
								<Thread
									user={ user }
									userRoles={ userRoles }
									slideType={ slideType }
									courseId={ courseId }
									animation={ animation }
									comment={ comment }
									commentPolicy={ commentPolicy }
									commentEditing={ commentEditing }
									reply={ reply }
									actions={ actions }
									getUserSolutionsUrl={ this.getUserSolutionsUrl }
									isSlideReady={ isSlideReady }/>
							</section>
						</CSSTransition>) }
			</TransitionGroup>
		)
	}

	findCommentPosition(id, threads) {
		for (let i = 0; i < threads.length; i++) {
			const thread = threads[i];
			if(thread.id === id) {
				return [threads, i];
			}

			if(!thread.replies) continue;

			for (let j = 0; j < thread.replies.length; j++) {
				const reply = thread.replies[j];
				if(reply.id === id) {
					return [thread.replies, j];
				}
			}
		}
	}

	findComment(id, threads) {
		const [parentArray, index] = this.findCommentPosition(id, threads);

		return parentArray[index];
	}

	deleteComment(id, threads) {
		const [parentArray, index] = this.findCommentPosition(id, threads);
		const comment = parentArray[index];
		parentArray.splice(index, 1);

		function restoreComment() {
			parentArray.splice(index, 0, comment);
		}

		return restoreComment;
	}

	compareThreads(a, b) {
		if(a.isPinnedToTop && !b.isPinnedToTop) {
			return -1;
		}

		if(!a.isPinnedToTop && b.isPinnedToTop) {
			return 1;
		}

		if(a.publishTime > b.publishTime) {
			return -1;
		}

		if(a.publishTime < b.publishTime) {
			return 1;
		}

		return 0;
	}

	updateComment(id, reducer) {
		const threads = [...this.state.threads];
		const comment = this.findComment(id, threads);

		Object.assign(comment, reducer(comment));

		if(!comment.parentCommentId) {
			threads.sort(this.compareThreads)
		}

		this.setState({ threads });
	}

	handleAddComment = async (commentId, text) => {
		const { commentsApi, courseId, slideId, forInstructors, handleInstructorsCommentCount } = this.props;
		const threads = [...this.state.threads];

		this.setState({
			sending: true,
			animation: true,
		});

		try {
			const newComment = await commentsApi.addComment(courseId, slideId, text, null, forInstructors);
			const pinnedComments = threads.filter(comment => comment.isPinnedToTop);
			const filteredComments = threads.filter(comment => !comment.isPinnedToTop)
				.sort((a, b) => new Date(b.publishTime) - new Date(a.publishTime));

			this.setState({
				threads: [
					...pinnedComments,
					newComment,
					...filteredComments,
				],
				newCommentId: this.state.newCommentId + 1,
			});

			this.handleScroll(this.lastThreadRef);

			if(forInstructors) {
				handleInstructorsCommentCount("add");
			}
		} catch (e) {
			Toast.push("Не удалось добавить комментарий. Попробуйте снова.");
			this.setState({
				newCommentId: this.state.newCommentId,
			});
			console.error(e);
		} finally {
			this.setState({
				sending: false,
			})
		}
	};

	handleLikeClick = (commentId, isLiked) => {
		const { commentsApi } = this.props;

		this.updateComment(commentId, ({ likesCount }) => ({
			likesCount: isLiked ? likesCount - 1 : likesCount + 1,
			isLiked: !isLiked,
		}));

		this.debouncedSendData(isLiked ? commentsApi.dislikeComment : commentsApi.likeComment, commentId);
	};

	handleApprovedMark = (commentId, isApproved) => {
		this.updateComment(commentId, () => ({
			isApproved: !isApproved,
		}));

		this.debouncedSendData(this.props.commentsApi.updateComment, commentId, { "isApproved": !isApproved });
	};

	handleCorrectAnswerMark = (commentId, isCorrectAnswer) => {
		this.updateComment(commentId, () => ({
			isCorrectAnswer: !isCorrectAnswer,
		}));

		this.debouncedSendData(this.props.commentsApi.updateComment, commentId, { "isCorrectAnswer": !isCorrectAnswer });
	};

	handlePinnedToTopMark = (commentId, isPinnedToTop) => {
		this.updateComment(commentId, () => ({
			isPinnedToTop: !isPinnedToTop,
		}));

		this.debouncedSendData(this.props.commentsApi.updateComment, commentId, { isPinnedToTop: !isPinnedToTop });
	};

	handleShowSendForm = () => {
		this.handleScroll(this.props.headerRef);
		this.setState({
			showFocus: true,
		});
	};

	handleShowEditForm = (commentId) => {
		this.setState({
			commentEditing: {
				...this.state.commentEditing,
				commentId: commentId,
			},
		});
	};

	handleShowReplyForm = (commentId) => {
		this.setState({
			reply: {
				...this.state.reply,
				commentId: commentId,
			},
		});
	};

	handleEditComment = async (commentId, text) => {
		const { commentsApi } = this.props;

		this.setState({
			commentEditing: {
				commentId: commentId,
				sending: true,
			},
		});

		try {
			const newComment = await commentsApi.updateComment(commentId, { "text": text });

			this.updateComment(commentId, () => ({
				text: text,
				renderedText: newComment.renderedText,
			}));

			this.setState({
				commentEditing: {
					commentId: null,
				},
			});
		} catch (e) {
			Toast.push("Не удалось изменить комментарий. Попробуйте снова.");
			this.setState({
				commentEditing: {
					commentId: commentId,
				},
			});
			console.error(e);
		} finally {
			this.setState({
				commentEditing: {
					sending: false,
				},
			})
		}
	};

	handleAddReplyComment = async (commentId, text) => {
		const { commentsApi, courseId, slideId, forInstructors } = this.props;

		this.setState({
			animation: true,
			reply: {
				commentId: commentId,
				sending: true,
			}
		});

		try {
			const newReply = await commentsApi.addComment(courseId, slideId, text, commentId, forInstructors);
			const index = this.state.threads.findIndex(comment => comment.id === commentId);
			const comment = this.state.threads[index];
			const newReplies = comment.replies.concat(newReply);
			const newComment = { ...comment, replies: newReplies };

			this.setState({
				threads: [
					...this.state.threads.slice(0, index),
					newComment,
					...this.state.threads.slice(index + 1),
				],
				reply: {
					commentId: null,
				},
			});
		} catch (e) {
			Toast.push("Не удалось отправить комментарий. Попробуйте снова.");
			this.setState({
				reply: {
					commentId: commentId,
				},
			});
			console.error(e);
		} finally {
			this.setState({
				reply: {
					sending: false,
				}
			});
		}
	};

	handleDeleteComment = (comment) => {
		const threads = JSON.parse(JSON.stringify(this.state.threads));
		const { forInstructors, handleInstructorsCommentCount } = this.props;

		this.setState({
			animation: true,
		});

		const restoreComment = this.deleteComment(comment.id, threads);
		this.setState({ threads });

		try {
			this.props.commentsApi.deleteComment(comment.id);

			if(forInstructors && !comment.parentCommentId) {
				handleInstructorsCommentCount("delete");
			}

			Toast.push("Комментарий удалён", {
				label: "Восстановить",
				handler: () => {
					restoreComment();
					this.setState({ threads });

					if(forInstructors && !comment.parentCommentId) {
						handleInstructorsCommentCount("add");
					}

					Toast.push("Комментарий восстановлен");

					this.props.commentsApi.updateComment(comment.id);
				}
			})
		} catch (e) {
			Toast.push("Комментарий не удалён. Произошла ошибка, попробуйте снова");
			this.setState({ threads });
			console.error(e);
		}
	};

	handleScroll = (ref) => {
		scrollToView(ref);
	};

	getUserSolutionsUrl = (userId) => {
		const { courseId, slideId } = this.props;
		return `${ window.location.origin }/Analytics/UserSolutions?courseId=${ courseId }&slideId=${ slideId }&userId=${ userId }`;
	};

	sendData = (method, commentId, property) =>
		method(commentId, property)
			.catch(e => {
				Toast.push("Не удалось изменить комментарий. Произошла ошибка, попробуйте снова");
				console.error(e);
			});
}

CommentsList.propTypes = {
	key: PropTypes.string,
	user: userType.isRequired,
	userRoles: userRoles.isRequired,
	slideId: PropTypes.string.isRequired,
	slideType: PropTypes.string.isRequired,
	forInstructors: PropTypes.bool,
	handleInstructorsCommentCount: PropTypes.func,
	commentsApi: PropTypes.objectOf(PropTypes.func),
	commentPolicy: commentPolicy,
	courseId: PropTypes.string.isRequired,
	isSlideReady: PropTypes.bool,
};

export default CommentsList;
